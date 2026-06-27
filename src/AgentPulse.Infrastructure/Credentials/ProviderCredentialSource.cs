namespace AgentPulse.Infrastructure.Credentials;

public enum ProviderCredentialSource
{
    Environment = 0,
    Prompt = 1,
    Stored = 2,
    LegacyStored = 3,
}
