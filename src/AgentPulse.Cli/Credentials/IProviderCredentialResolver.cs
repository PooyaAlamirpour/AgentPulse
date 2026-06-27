using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Cli.Credentials;

public interface IProviderCredentialResolver
{
    Task ResolveForRunAsync(
        IProviderCredentialSession credentialSession,
        CancellationToken cancellationToken = default);
}
