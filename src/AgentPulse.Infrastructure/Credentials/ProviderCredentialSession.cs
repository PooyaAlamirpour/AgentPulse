namespace AgentPulse.Infrastructure.Credentials;

public sealed class ProviderCredentialSession : IProviderCredentialSession
{
    private readonly IProviderCredentialStore _credentialStore;
    private readonly ILegacyProviderCredentialStore _legacyCredentialStore;
    private readonly ProviderCredentialScope _scope;
    private string? _credential;
    private ProviderCredentialSource _source;
    private bool _accepted;
    private bool _rejected;

    public ProviderCredentialSession(IProviderCredentialStore credentialStore)
        : this(
            credentialStore,
            NullLegacyProviderCredentialStore.Instance,
            ProviderCredentialScope.XiaomiDefault)
    {
    }

    public ProviderCredentialSession(
        IProviderCredentialStore credentialStore,
        ProviderCredentialScope scope)
        : this(credentialStore, NullLegacyProviderCredentialStore.Instance, scope)
    {
    }

    public ProviderCredentialSession(
        IProviderCredentialStore credentialStore,
        ILegacyProviderCredentialStore legacyCredentialStore,
        ProviderCredentialScope scope)
    {
        _credentialStore = credentialStore ??
            throw new ArgumentNullException(nameof(credentialStore));
        _legacyCredentialStore = legacyCredentialStore ??
            throw new ArgumentNullException(nameof(legacyCredentialStore));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    public void Set(string credential, ProviderCredentialSource source)
    {
        var normalizedCredential = ProviderCredentialValidator.ValidateAndNormalize(credential);

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown credential source.");
        }

        if (_credential is not null)
        {
            throw new InvalidOperationException(
                "A provider credential has already been configured for this run.");
        }

        _credential = normalizedCredential;
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

        switch (_source)
        {
            case ProviderCredentialSource.Prompt:
                await _credentialStore.SaveAsync(_scope, _credential, cancellationToken);
                break;
            case ProviderCredentialSource.LegacyStored:
                await _credentialStore.SaveAsync(_scope, _credential, cancellationToken);
                await _legacyCredentialStore.DeleteLegacyAsync(cancellationToken);
                break;
            case ProviderCredentialSource.Environment:
            case ProviderCredentialSource.Stored:
                break;
            default:
                throw new InvalidOperationException("The credential source is not supported.");
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

        if (_rejected)
        {
            return;
        }

        switch (_source)
        {
            case ProviderCredentialSource.Stored:
                await _credentialStore.DeleteAsync(_scope, cancellationToken);
                break;
            case ProviderCredentialSource.LegacyStored:
                await _credentialStore.DeleteAsync(_scope, cancellationToken);
                await _legacyCredentialStore.DeleteLegacyAsync(cancellationToken);
                break;
            case ProviderCredentialSource.Environment:
            case ProviderCredentialSource.Prompt:
                break;
            default:
                throw new InvalidOperationException("The credential source is not supported.");
        }

        _rejected = true;
    }

    private sealed class NullLegacyProviderCredentialStore : ILegacyProviderCredentialStore
    {
        public static readonly NullLegacyProviderCredentialStore Instance = new();

        public Task<string?> GetLegacyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task DeleteLegacyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> LegacyExistsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }
}
