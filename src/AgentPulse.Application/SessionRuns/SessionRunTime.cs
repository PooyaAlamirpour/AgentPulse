using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Application.SessionRuns;

internal static class SessionRunTime
{
    public static DateTime GetUtcNow(IClock clock)
    {
        var utcNow = clock.UtcNow;
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidUtcClock,
                "The configured clock returned a non-UTC timestamp.");
        }

        return utcNow;
    }
}
