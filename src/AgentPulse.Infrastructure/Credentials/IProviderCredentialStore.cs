namespace AgentPulse.Infrastructure.Credentials;

public interface IProviderCredentialStore
{
    Task<string?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string credential, CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
}
