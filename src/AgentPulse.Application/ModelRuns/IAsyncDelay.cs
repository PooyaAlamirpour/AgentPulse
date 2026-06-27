namespace AgentPulse.Application.ModelRuns;

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}
