namespace AgentPulse.Cli.Credentials;

public interface IEnvironmentVariableReader
{
    string? Get(string name);
}
