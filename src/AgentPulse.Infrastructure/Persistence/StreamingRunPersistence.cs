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

        GetTextPart(message).ReplaceText(completeText, utcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task CompleteAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        CancellationToken cancellationToken = default)
    {
        return FinalizeAsync(
            sessionId,
            assistantMessageId,
            leaseId,
            completeText,
            FinalState.Completed,
            failureReason: null,
            cancellationToken);
    }

    public Task FailAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        string failureReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        return FinalizeAsync(
            sessionId,
            assistantMessageId,
            leaseId,
            completeText,
            FinalState.Failed,
            failureReason.Trim(),
            cancellationToken);
    }

    public Task CancelAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        CancellationToken cancellationToken = default)
    {
        return FinalizeAsync(
            sessionId,
            assistantMessageId,
            leaseId,
            completeText,
            FinalState.Cancelled,
            failureReason: null,
            cancellationToken);
    }

    private async Task FinalizeAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        FinalState finalState,
        string? failureReason,
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

        var session = await dbContext.Sessions.SingleOrDefaultAsync(
                value => value.Id == sessionId,
                cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.SessionNotFound,
                $"Session '{sessionId}' does not exist or is no longer available.");

        var runLease = await dbContext.RunLeases.SingleOrDefaultAsync(
            value => value.SessionId == sessionId,
            cancellationToken);

        if (runLease is not null && runLease.LeaseId != leaseId)
        {
            throw new SessionRunException(
                SessionRunErrorCode.RunLeaseOwnershipMismatch,
                "The run lease can only be released by its owner.");
        }

        if (finalState == FinalState.Completed && runLease is null)
        {
            throw new SessionRunException(
                SessionRunErrorCode.RunLeaseNotFound,
                $"Session '{sessionId}' has no active run lease.");
        }

        GetTextPart(message).ReplaceText(completeText, utcNow);
        ApplyMessageState(message, finalState, failureReason, utcNow);

        if (runLease is not null)
        {
            dbContext.RunLeases.Remove(runLease);
        }

        if (session.Status == SessionStatus.Running)
        {
            session.Stop(utcNow);
        }

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
        string? failureReason,
        DateTime utcNow)
    {
        if (message.Status != MessageStatus.Streaming)
        {
            var expectedStatus = finalState switch
            {
                FinalState.Completed => MessageStatus.Completed,
                FinalState.Failed => MessageStatus.Failed,
                FinalState.Cancelled => MessageStatus.Cancelled,
                _ => throw new ArgumentOutOfRangeException(nameof(finalState)),
            };

            if (message.Status == expectedStatus)
            {
                return;
            }

            throw new SessionRunException(
                SessionRunErrorCode.InvalidSessionState,
                $"Assistant message '{message.Id}' is not streaming.");
        }

        switch (finalState)
        {
            case FinalState.Completed:
                message.Complete(utcNow);
                break;
            case FinalState.Failed:
                message.Fail(failureReason, utcNow);
                break;
            case FinalState.Cancelled:
                message.Cancel(utcNow);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(finalState));
        }
    }

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
