namespace AgentPulse.Cli.Credentials;

public interface ISecretInputReader
{
    Task<string> ReadAsync(CancellationToken cancellationToken = default);
}
