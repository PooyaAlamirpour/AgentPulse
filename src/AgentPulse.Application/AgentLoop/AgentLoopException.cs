namespace AgentPulse.Application.AgentLoop;

public sealed class AgentLoopException : Exception
{
    public AgentLoopException(AgentLoopErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public AgentLoopErrorCode Code { get; }
}
