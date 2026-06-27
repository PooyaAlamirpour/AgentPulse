namespace AgentPulse.Infrastructure.Credentials;

public sealed class ProviderCredentialSession : IProviderCredentialSession
{
    private readonly IProviderCredentialStore _credentialStore;
    private readonly ProviderCredentialScope _scope;
    private string? _credential;
    private ProviderCredentialSource _source;
    private bool _accepted;

    public ProviderCredentialSession(IProviderCredentialStore credentialStore)
        : this(credentialStore, ProviderCredentialScope.XiaomiDefault)
    {
    }

    public ProviderCredentialSession(
        IProviderCredentialStore credentialStore,
        ProviderCredentialScope scope)
    {
        _credentialStore = credentialStore ??
            throw new ArgumentNullException(nameof(credentialStore));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

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

        if (_source is ProviderCredentialSource.Prompt or ProviderCredentialSource.Stored)
        {
            await _credentialStore.SaveAsync(_scope, _credential, cancellationToken);
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
            await _credentialStore.DeleteAsync(_scope, cancellationToken);
        }
    }
}
