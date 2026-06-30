namespace AgentPulse.Application.AgentLoop;

public enum AgentLoopErrorCode
{
    InvalidResponse = 1,
    MaxIterationsReached = 2,
    ProviderFailure = 3,
    NoProgress = 4,
}
