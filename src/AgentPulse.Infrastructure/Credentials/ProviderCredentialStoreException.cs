namespace AgentPulse.Infrastructure.Credentials;

public sealed class ProviderCredentialStoreException : Exception
{
    public ProviderCredentialStoreException(string message)
        : base(message)
    {
    }

    public ProviderCredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
