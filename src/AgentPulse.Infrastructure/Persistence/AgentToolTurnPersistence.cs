using System.Text.Json;
using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Infrastructure.Persistence;

public sealed class AgentToolTurnPersistence(
    IDbContextFactory<AgentPulseDbContext> dbContextFactory,
    IClock clock,
    ILogger<AgentToolTurnPersistence> logger,
    AgentToolOptions? toolOptions = null) : IAgentToolTurnPersistence
{
    public async Task SaveAssistantToolCallsAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string model,
        ChatModelResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(response);
        if (response.ToolCalls.Count == 0)
        {
            throw new ArgumentException("A tool turn must contain at least one tool call.", nameof(response));
        }

        var utcNow = GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateLeaseAsync(dbContext, sessionId, leaseId, cancellationToken);
        var assistant = await dbContext.Messages
            .Include(value => value.Parts.OrderBy(part => part.Order))
            .SingleOrDefaultAsync(value => value.Id == assistantMessageId, cancellationToken)
            ?? throw InvalidState($"Assistant message '{assistantMessageId}' does not exist.");

        if (assistant.SessionId != sessionId || assistant.Role != MessageRole.Assistant ||
            assistant.Status != MessageStatus.Streaming || !string.Equals(assistant.Model, model, StringComparison.Ordinal))
        {
            throw InvalidState("The active assistant message does not match the current tool turn.");
        }

        var textPart = assistant.Parts.OfType<TextMessagePart>().SingleOrDefault()
            ?? throw InvalidState("The active assistant message does not contain its text buffer.");
        textPart.ReplaceText(response.Text ?? string.Empty, utcNow);
        var partOrder = assistant.Parts.Count + 1;
        foreach (var call in response.ToolCalls.OrderBy(static value => value.Order))
        {
            assistant.AddToolCallPart(MessagePartId.New(), partOrder++, call.Id, call.Name, call.ArgumentsJson, utcNow);
        }

        var usage = response.Usage;
        assistant.Complete(ModelFinishReason.ToolCalls.ToString(), usage?.InputTokens, usage?.OutputTokens, usage?.TotalTokens, utcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveToolResultAsync(
        SessionId sessionId,
        MessageId assistantToolCallMessageId,
        RunLeaseId leaseId,
        AgentLoopToolExecution execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (string.IsNullOrWhiteSpace(execution.Call.Id))
        {
            throw InvalidState("Tool result call identifier is required.");
        }

        var utcNow = GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (!await dbContext.Sessions.AnyAsync(value => value.Id == sessionId, cancellationToken))
        {
            logger.LogWarning("Rejected tool result for missing session {SessionId}.", sessionId);
            throw InvalidState($"Session '{sessionId}' does not exist.");
        }

        await ValidateLeaseAsync(dbContext, sessionId, leaseId, cancellationToken);

        var assistant = await dbContext.Messages
            .Include(value => value.Parts.OrderBy(part => part.Order))
            .SingleOrDefaultAsync(value => value.Id == assistantToolCallMessageId, cancellationToken);
        if (assistant is null || assistant.SessionId != sessionId ||
            assistant.Role != MessageRole.Assistant || assistant.Status != MessageStatus.Completed)
        {
            logger.LogWarning(
                "Rejected tool result because assistant tool turn {AssistantMessageId} is invalid for session {SessionId}.",
                assistantToolCallMessageId,
                sessionId);
            throw InvalidState("The assistant tool-call message does not belong to the requested session and completed tool turn.");
        }

        var toolCall = assistant.Parts.OfType<ToolCallMessagePart>()
            .SingleOrDefault(part => string.Equals(part.ToolCallId, execution.Call.Id, StringComparison.Ordinal));
        if (toolCall is null)
        {
            logger.LogWarning(
                "Rejected tool result for unknown call {ToolCallId} in assistant turn {AssistantMessageId} and session {SessionId}.",
                execution.Call.Id,
                assistantToolCallMessageId,
                sessionId);
            throw InvalidState("The tool call does not exist in the specified assistant tool turn.");
        }

        if (!string.Equals(toolCall.ToolName, execution.Call.Name, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Rejected tool result for call {ToolCallId} because tool name {ActualToolName} does not match {ExpectedToolName}.",
                execution.Call.Id,
                execution.Call.Name,
                toolCall.ToolName);
            throw InvalidState("The tool result name does not match the persisted tool call name.");
        }

        if (await ToolResultExistsAsync(
                dbContext, sessionId, assistantToolCallMessageId, execution.Call.Id, cancellationToken))
        {
            logger.LogInformation(
                "Detected duplicate tool result for session {SessionId}, assistant turn {AssistantMessageId}, and call {ToolCallId}.",
                sessionId,
                assistantToolCallMessageId,
                execution.Call.Id);
            return;
        }

        var sequence = await dbContext.Messages
            .Where(value => value.SessionId == sessionId)
            .MaxAsync(value => value.Sequence, cancellationToken);
        var toolMessage = new Message(MessageId.New(), sessionId, MessageRole.Tool, sequence + 1, utcNow);
        var limit = AgentToolResultLimiter.Limit(
            execution.Result,
            (toolOptions ?? new AgentToolOptions()).MaxOutputCharacters);
        if (limit.WasLimited)
        {
            logger.LogInformation(
                "Tool result was limited before persistence for tool {ToolName}.",
                execution.Call.Name);
        }

        var boundedResult = limit.Result;
        var metadataJson = JsonSerializer.Serialize(new
        {
            durationMs = execution.Duration.TotalMilliseconds,
            values = boundedResult.Metadata,
        });
        toolMessage.AddToolResultPart(
            MessagePartId.New(), sessionId, assistantToolCallMessageId, 1,
            execution.Call.Id, execution.Call.Name,
            boundedResult.Succeeded, boundedResult.Output, boundedResult.Error, metadataJson, utcNow);
        toolMessage.Complete(utcNow);
        await dbContext.Messages.AddAsync(toolMessage, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await using var verificationContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (await ToolResultExistsAsync(
                    verificationContext, sessionId, assistantToolCallMessageId, execution.Call.Id, cancellationToken))
            {
                logger.LogInformation(
                    "Unique constraint resolved concurrent duplicate tool result for session {SessionId}, assistant turn {AssistantMessageId}, and call {ToolCallId}.",
                    sessionId,
                    assistantToolCallMessageId,
                    execution.Call.Id);
                return;
            }

            throw;
        }
    }

    public async Task<MessageId> StartNextAssistantMessageAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        string model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var utcNow = GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateLeaseAsync(dbContext, sessionId, leaseId, cancellationToken);
        var sequence = await dbContext.Messages
            .Where(value => value.SessionId == sessionId)
            .MaxAsync(value => value.Sequence, cancellationToken);
        var nextAssistant = new Message(MessageId.New(), sessionId, MessageRole.Assistant, sequence + 1, utcNow);
        nextAssistant.AddTextPart(MessagePartId.New(), 1, string.Empty, utcNow);
        nextAssistant.StartStreaming(model, utcNow);
        await dbContext.Messages.AddAsync(nextAssistant, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return nextAssistant.Id;
    }

    private static Task<bool> ToolResultExistsAsync(
        AgentPulseDbContext dbContext,
        SessionId sessionId,
        MessageId assistantToolCallMessageId,
        string toolCallId,
        CancellationToken cancellationToken) =>
        dbContext.MessageParts.OfType<ToolResultMessagePart>().AnyAsync(
            part => part.SessionId == sessionId &&
                    part.AssistantToolCallMessageId == assistantToolCallMessageId &&
                    part.ToolCallId == toolCallId,
            cancellationToken);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 2067,
        };

    private static async Task ValidateLeaseAsync(
        AgentPulseDbContext dbContext,
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken)
    {
        var lease = await dbContext.RunLeases.SingleOrDefaultAsync(value => value.SessionId == sessionId, cancellationToken)
            ?? throw new SessionRunException(SessionRunErrorCode.RunLeaseNotFound, $"Session '{sessionId}' has no active run lease.");
        if (lease.LeaseId != leaseId)
        {
            throw new SessionRunException(SessionRunErrorCode.RunLeaseOwnershipMismatch, "The run lease can only be updated by its owner.");
        }
    }

    private static SessionRunException InvalidState(string message) =>
        new(SessionRunErrorCode.InvalidSessionState, message);

    private DateTime GetUtcNow()
    {
        var value = clock.UtcNow;
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new SessionRunException(SessionRunErrorCode.InvalidUtcClock, "The configured clock returned a non-UTC timestamp.");
        }

        return value;
    }
}
