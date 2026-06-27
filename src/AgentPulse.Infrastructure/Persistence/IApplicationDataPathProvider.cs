namespace AgentPulse.Infrastructure.Persistence;

public interface IApplicationDataPathProvider
{
    string ResolveDatabasePath(string? configuredDatabasePath);
}
