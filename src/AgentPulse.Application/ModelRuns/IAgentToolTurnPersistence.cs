using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.ChatModels;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.ModelRuns;

public interface IAgentToolTurnPersistence
{
    Task SaveAssistantToolCallsAsync(
        SessionId sessionId,
        MessageId assistantMessageId,
        RunLeaseId leaseId,
        string model,
        ChatModelResponse response,
        CancellationToken cancellationToken = default);

    Task SaveToolResultAsync(
        SessionId sessionId,
        MessageId assistantToolCallMessageId,
        RunLeaseId leaseId,
        AgentLoopToolExecution execution,
        CancellationToken cancellationToken = default);

    Task<MessageId> StartNextAssistantMessageAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        string model,
        CancellationToken cancellationToken = default);
}
