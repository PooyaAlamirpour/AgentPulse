namespace AgentPulse.Infrastructure.Credentials;

public sealed class ProviderCredentialValidationException : Exception
{
    public ProviderCredentialValidationException()
        : base("The configured API credential contains invalid characters.")
    {
    }
}
