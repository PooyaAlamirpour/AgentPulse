namespace AgentPulse.Infrastructure.Persistence;

public interface IAgentPulseDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
