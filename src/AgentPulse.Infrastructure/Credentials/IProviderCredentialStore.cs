namespace AgentPulse.Infrastructure.Credentials;

public interface IProviderCredentialStore
{
    Task<string?> GetAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ProviderCredentialScope scope,
        string credential,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default);
}
