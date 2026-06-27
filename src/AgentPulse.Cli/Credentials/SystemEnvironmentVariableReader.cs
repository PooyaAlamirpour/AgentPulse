namespace AgentPulse.Cli.Credentials;

public sealed class SystemEnvironmentVariableReader : IEnvironmentVariableReader
{
    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Environment.GetEnvironmentVariable(name);
    }
}
