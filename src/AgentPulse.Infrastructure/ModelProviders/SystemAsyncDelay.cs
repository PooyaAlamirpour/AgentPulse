using AgentPulse.Application.ModelRuns;

namespace AgentPulse.Infrastructure.ModelProviders;

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        return Task.Delay(delay, cancellationToken);
    }
}
