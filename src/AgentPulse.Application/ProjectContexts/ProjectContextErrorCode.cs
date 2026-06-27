namespace AgentPulse.Application.ProjectContexts;

public enum ProjectContextErrorCode
{
    InvalidPath = 1,
    PathNotFound = 2,
    PathIsNotDirectory = 3,
    PathAccessDenied = 4,
    GitProcessTimedOut = 5,
    GitProcessFailed = 6,
    InvalidUtcClock = 7,
}
