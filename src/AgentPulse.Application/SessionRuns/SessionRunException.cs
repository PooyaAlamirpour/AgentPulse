namespace AgentPulse.Application.SessionRuns;

public sealed class SessionRunException : Exception
{
    public SessionRunException(SessionRunErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public SessionRunException(SessionRunErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public SessionRunErrorCode Code { get; }
}
