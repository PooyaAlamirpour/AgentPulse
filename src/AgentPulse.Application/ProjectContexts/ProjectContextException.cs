namespace AgentPulse.Application.ProjectContexts;

public sealed class ProjectContextException : Exception
{
    public ProjectContextException(ProjectContextErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public ProjectContextException(
        ProjectContextErrorCode errorCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public ProjectContextErrorCode ErrorCode { get; }
}
