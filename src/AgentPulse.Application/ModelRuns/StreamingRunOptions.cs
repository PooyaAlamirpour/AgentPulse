namespace AgentPulse.Application.ModelRuns;

public sealed class StreamingRunOptions
{
    public const string SectionName = "AgentPulse:Streaming";

    public static readonly TimeSpan MinimumLeaseRenewInterval = TimeSpan.FromSeconds(1);

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public int FlushCharacterThreshold { get; set; } = 256;

    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (FlushInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Streaming flush interval must be positive.");
        }

        if (FlushCharacterThreshold <= 0)
        {
            throw new InvalidOperationException(
                "Streaming flush character threshold must be greater than zero.");
        }

        if (LeaseRenewInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Run lease renew interval must be positive.");
        }

        if (LeaseRenewInterval < MinimumLeaseRenewInterval)
        {
            throw new InvalidOperationException(
                $"Run lease renew interval must be at least {MinimumLeaseRenewInterval.TotalSeconds:0} second.");
        }

        if (CleanupTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Run cleanup timeout must be positive.");
        }
    }
}
