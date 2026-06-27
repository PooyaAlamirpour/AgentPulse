namespace AgentPulse.Cli.Credentials;

public sealed class CredentialResolutionException : Exception
{
    public CredentialResolutionException(string message)
        : base(message)
    {
    }
}
