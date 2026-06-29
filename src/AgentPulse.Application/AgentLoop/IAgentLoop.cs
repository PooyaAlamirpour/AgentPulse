namespace AgentPulse.Application.AgentLoop;

public interface IAgentLoop
{
    Task<AgentLoopResult> ExecuteAsync(
        AgentLoopRequest request,
        IAgentLoopObserver? observer = null,
        CancellationToken cancellationToken = default);
}
