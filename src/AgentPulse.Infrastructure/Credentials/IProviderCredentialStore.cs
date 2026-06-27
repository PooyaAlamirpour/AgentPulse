namespace AgentPulse.Infrastructure.Credentials;

public interface IProviderCredentialStore
{
    // Phase 6 compatibility methods operate on the legacy Xiaomi credential file.
    Task<string?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string credential, CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);

    Task<string?> GetAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return GetAsync(cancellationToken);
    }

    Task SaveAsync(
        ProviderCredentialScope scope,
        string credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return SaveAsync(credential, cancellationToken);
    }

    Task DeleteAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return DeleteAsync(cancellationToken);
    }

    Task<bool> ExistsAsync(
        ProviderCredentialScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return ExistsAsync(cancellationToken);
    }
}
