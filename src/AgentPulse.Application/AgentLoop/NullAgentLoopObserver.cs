using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentLoop;

public sealed class NullAgentLoopObserver : IAgentLoopObserver
{
    public static NullAgentLoopObserver Instance { get; } = new();

    private NullAgentLoopObserver() { }

    public Task RecordAssistantResponseAsync(ChatModelResponse response, int iteration, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordToolResultAsync(AgentLoopToolExecution result, int iteration, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteToolTurnAsync(int iteration, CancellationToken cancellationToken) => Task.CompletedTask;
}
