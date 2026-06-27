namespace AgentPulse.Infrastructure.Credentials;

public sealed class ProviderCredentialSession(IProviderCredentialStore credentialStore)
    : IProviderCredentialSession
{
    private string? _credential;
    private ProviderCredentialSource _source;
    private bool _accepted;

    public void Set(string credential, ProviderCredentialSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown credential source.");
        }

        if (_credential is not null)
        {
            throw new InvalidOperationException(
                "A provider credential has already been configured for this run.");
        }

        _credential = credential.Trim();
        _source = source;
    }

    public string GetRequiredCredential()
    {
        return _credential
            ?? throw new InvalidOperationException(
                "A provider credential was not configured for this run.");
    }

    public async Task MarkAcceptedAsync(CancellationToken cancellationToken = default)
    {
        if (_credential is null)
        {
            throw new InvalidOperationException(
                "A provider credential was not configured for this run.");
        }

        if (_accepted)
        {
            return;
        }

        if (_source == ProviderCredentialSource.Prompt)
        {
            await credentialStore.SaveAsync(_credential, cancellationToken);
        }

        _accepted = true;
    }

    public async Task MarkAuthenticationRejectedAsync(
        CancellationToken cancellationToken = default)
    {
        if (_credential is null)
        {
            throw new InvalidOperationException(
                "A provider credential was not configured for this run.");
        }

        if (_source == ProviderCredentialSource.Stored)
        {
            await credentialStore.DeleteAsync(cancellationToken);
        }
    }
}
