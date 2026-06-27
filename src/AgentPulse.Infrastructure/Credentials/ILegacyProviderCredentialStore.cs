namespace AgentPulse.Infrastructure.Credentials;

public interface ILegacyProviderCredentialStore
{
    Task<string?> GetLegacyAsync(CancellationToken cancellationToken = default);

    Task DeleteLegacyAsync(CancellationToken cancellationToken = default);

    Task<bool> LegacyExistsAsync(CancellationToken cancellationToken = default);
}
