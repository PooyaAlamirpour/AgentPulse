namespace AgentPulse.Application.SessionRuns;

public sealed class SessionRunOptions
{
    public const string SectionName = "AgentPulse:SessionRun";

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Session run lease duration must be positive.");
        }
    }
}
