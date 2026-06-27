using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence;

public sealed class StreamingRunPersistence(
    IDbContextFactory<AgentPulseDbContext> dbContextFactory,
    IClock clock) : IStreamingRunPersistence
{
    public async Task FlushAssistantTextAsync(
        MessageId assistantMessageId,
        string completeText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completeText);
        var utcNow = GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var message = await LoadAssistantMessageAsync(
            dbContext,
            assistantMessageId,
            cancellationToken);

        if (message.Status != MessageStatus.Streaming)
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidSessionState,
                $"Assistant message '{assistantMessageId}' is not streaming.");
        }

        var textPart = GetTextPart(message);
        if (string.Equals(textPart.Text, completeText, StringComparison.Ordinal))
        {
            return;
        }

        textPart.ReplaceText(completeText, utcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task CompleteAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        AssistantCompletionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return FinalizeAsync(
            sessionId,
            assistantMessageId,
            leaseId,
            completeText,
            FinalState.Completed,
            completionMetadata: metadata,
            failureMetadata: null,
            model: metadata.Model,
            cancellationToken: cancellationToken);
    }

    public Task FailAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        AssistantFailureMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return FinalizeAsync(
            sessionId,
            assistantMessageId,
            leaseId,
            completeText,
            FinalState.Failed,
            completionMetadata: null,
            failureMetadata: metadata,
            model: metadata.Model,
            cancellationToken: cancellationToken);
    }

    public Task CancelAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        string model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return FinalizeAsync(
            sessionId,
            assistantMessageId,
            leaseId,
            completeText,
            FinalState.Cancelled,
            completionMetadata: null,
            failureMetadata: null,
            model: model.Trim(),
            cancellationToken: cancellationToken);
    }

    private async Task FinalizeAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        FinalState finalState,
        AssistantCompletionMetadata? completionMetadata,
        AssistantFailureMetadata? failureMetadata,
        string model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completeText);
        var utcNow = GetUtcNow();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        var message = await LoadAssistantMessageAsync(
            dbContext,
            assistantMessageId,
            cancellationToken);

        if (message.SessionId != sessionId)
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidSessionState,
                "The assistant message does not belong to the active session.");
        }

        if (!string.Equals(message.Model, model, StringComparison.Ordinal))
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidSessionState,
                "The assistant message model does not match the active run.");
        }

        var runLease = await dbContext.RunLeases.SingleOrDefaultAsync(
            value => value.SessionId == sessionId,
            cancellationToken);

        if (runLease is null)
        {
            throw new SessionRunException(
                SessionRunErrorCode.RunLeaseNotFound,
                $"Session '{sessionId}' has no active run lease.");
        }

        if (runLease.LeaseId != leaseId)
        {
            throw new SessionRunException(
                SessionRunErrorCode.RunLeaseOwnershipMismatch,
                "The run lease can only be finalized by its owner.");
        }

        var textPart = GetTextPart(message);
        var expectedStatus = GetExpectedStatus(finalState);
        if (message.Status == expectedStatus)
        {
            if (!string.Equals(textPart.Text, completeText, StringComparison.Ordinal))
            {
                throw new SessionRunException(
                    SessionRunErrorCode.InvalidSessionState,
                    $"Assistant message '{message.Id}' was already finalized with different content.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (message.Status != MessageStatus.Streaming)
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidSessionState,
                $"Assistant message '{message.Id}' is not streaming.");
        }

        if (!string.Equals(textPart.Text, completeText, StringComparison.Ordinal))
        {
            textPart.ReplaceText(completeText, utcNow);
        }

        ApplyMessageState(
            message,
            finalState,
            completionMetadata,
            failureMetadata,
            utcNow);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Message> LoadAssistantMessageAsync(
        AgentPulseDbContext dbContext,
        MessageId assistantMessageId,
        CancellationToken cancellationToken)
    {
        var message = await dbContext.Messages
            .Include(value => value.Parts.OrderBy(part => part.Order))
            .SingleOrDefaultAsync(value => value.Id == assistantMessageId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Assistant message '{assistantMessageId}' does not exist.");

        if (message.Role != MessageRole.Assistant)
        {
            throw new InvalidOperationException(
                $"Message '{assistantMessageId}' is not an assistant message.");
        }

        return message;
    }

    private static TextMessagePart GetTextPart(Message message)
    {
        if (message.Parts.Count != 1 || message.Parts.Single() is not TextMessagePart textPart)
        {
            throw new InvalidOperationException(
                $"Assistant message '{message.Id}' does not have exactly one text part.");
        }

        return textPart;
    }

    private static void ApplyMessageState(
        Message message,
        FinalState finalState,
        AssistantCompletionMetadata? completionMetadata,
        AssistantFailureMetadata? failureMetadata,
        DateTime utcNow)
    {
        switch (finalState)
        {
            case FinalState.Completed:
                var usage = completionMetadata!.Usage;
                message.Complete(
                    completionMetadata.FinishReason.ToString(),
                    usage?.InputTokens,
                    usage?.OutputTokens,
                    usage?.TotalTokens,
                    utcNow);
                break;
            case FinalState.Failed:
                message.Fail(
                    failureMetadata!.Reason,
                    failureMetadata.Kind,
                    failureMetadata.Stage,
                    failureMetadata.StatusCode,
                    utcNow);
                break;
            case FinalState.Cancelled:
                message.Cancel(ModelFinishReason.Cancelled.ToString(), utcNow);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(finalState));
        }
    }

    private static MessageStatus GetExpectedStatus(FinalState finalState) => finalState switch
    {
        FinalState.Completed => MessageStatus.Completed,
        FinalState.Failed => MessageStatus.Failed,
        FinalState.Cancelled => MessageStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(finalState)),
    };

    private DateTime GetUtcNow()
    {
        var utcNow = clock.UtcNow;
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidUtcClock,
                "The configured clock returned a non-UTC timestamp.");
        }

        return utcNow;
    }

    private enum FinalState
    {
        Completed,
        Failed,
        Cancelled,
    }
}
