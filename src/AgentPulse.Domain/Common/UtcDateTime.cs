namespace AgentPulse.Domain.Common;

internal static class UtcDateTime
{
    public static DateTime Ensure(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must use DateTimeKind.Utc.", parameterName);
        }

        return value;
    }

    public static DateTime EnsureNotBefore(
        DateTime value,
        DateTime minimum,
        string parameterName)
    {
        Ensure(value, parameterName);

        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Timestamp cannot be earlier than the entity creation time.");
        }

        return value;
    }
}
