namespace AgentPulse.Application.ModelRuns;

public enum ModelRunErrorCode
{
    ProviderFailure = 1,
    InvalidStream = 2,
    LeaseLost = 3,
    PersistenceFailure = 4,
    ProviderCancelled = 5,
}
