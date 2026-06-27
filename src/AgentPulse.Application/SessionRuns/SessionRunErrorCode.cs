namespace AgentPulse.Application.SessionRuns;

public enum SessionRunErrorCode
{
    InvalidUserPrompt = 1,
    ProjectNotFound = 2,
    SessionNotFound = 3,
    SessionProjectMismatch = 4,
    SessionAlreadyRunning = 5,
    InvalidSessionState = 6,
    RunLeaseNotFound = 7,
    RunLeaseOwnershipMismatch = 8,
    RunLeaseExpired = 9,
    InvalidUtcClock = 10,
}
