using AgentPulse.Domain.Messages;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.ModelRuns;

public interface IStreamingRunPersistence
{
    Task FlushAssistantTextAsync(
        MessageId assistantMessageId,
        string completeText,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        string failureReason,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string completeText,
        CancellationToken cancellationToken = default);
}
