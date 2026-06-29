using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentLoop;

public interface IAgentLoopObserver
{
    Task RecordAssistantResponseAsync(
        ChatModelResponse response,
        int iteration,
        CancellationToken cancellationToken);

    Task RecordToolResultAsync(
        AgentLoopToolExecution result,
        int iteration,
        CancellationToken cancellationToken);

    Task CompleteToolTurnAsync(
        int iteration,
        CancellationToken cancellationToken);
}
