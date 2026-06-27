using AgentPulse.Application.SessionRuns;

namespace AgentPulse.Application.ModelRuns;

public static class RunLeaseOptionsValidator
{
    public static void Validate(
        SessionRunOptions sessionRunOptions,
        StreamingRunOptions streamingRunOptions)
    {
        ArgumentNullException.ThrowIfNull(sessionRunOptions);
        ArgumentNullException.ThrowIfNull(streamingRunOptions);

        sessionRunOptions.Validate();
        streamingRunOptions.Validate();

        if (streamingRunOptions.LeaseRenewInterval > sessionRunOptions.LeaseDuration / 2)
        {
            throw new InvalidOperationException(
                "Run lease renew interval must be less than or equal to half of the session run lease duration.");
        }
    }
}
