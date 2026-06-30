namespace AgentPulse.Application.AgentTools;

public enum AgentToolFailureClassification
{
    None = 0,
    Deterministic = 1,
    Transient = 2,
    Cancelled = 3,
    Unknown = 4,
}
