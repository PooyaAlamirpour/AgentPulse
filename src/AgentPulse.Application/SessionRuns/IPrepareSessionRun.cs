namespace AgentPulse.Application.SessionRuns;

public interface IPrepareSessionRun
{
    Task<PrepareSessionRunResult> ExecuteAsync(
        PrepareSessionRunRequest request,
        CancellationToken cancellationToken = default);
}
