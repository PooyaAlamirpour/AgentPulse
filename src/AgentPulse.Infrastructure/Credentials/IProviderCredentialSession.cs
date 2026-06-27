namespace AgentPulse.Infrastructure.Credentials;

public interface IProviderCredentialSession
{
    void Set(string credential, ProviderCredentialSource source);

    string GetRequiredCredential();

    Task MarkAcceptedAsync(CancellationToken cancellationToken = default);

    Task MarkAuthenticationRejectedAsync(CancellationToken cancellationToken = default);
}
