using AgentPulse.Cli.Console;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Cli.Credentials;

public sealed class ProviderCredentialResolver : IProviderCredentialResolver
{
    public const string EnvironmentVariableName =
        OpenAiCompatibleModelOptions.XiaomiDefaultApiKeyEnvironmentVariable;

    private readonly IEnvironmentVariableReader _environmentVariables;
    private readonly IProviderCredentialStore _credentialStore;
    private readonly ILegacyProviderCredentialStore _legacyCredentialStore;
    private readonly ISecretInputReader _secretInputReader;
    private readonly IConsole _console;
    private readonly OpenAiCompatibleModelOptions _options;
    private readonly ProviderCredentialScope _scope;

    public ProviderCredentialResolver(
        IEnvironmentVariableReader environmentVariables,
        IProviderCredentialStore credentialStore,
        ISecretInputReader secretInputReader,
        IConsole console)
        : this(
            environmentVariables,
            credentialStore,
            NullLegacyProviderCredentialStore.Instance,
            secretInputReader,
            console,
            new OpenAiCompatibleModelOptions())
    {
    }

    public ProviderCredentialResolver(
        IEnvironmentVariableReader environmentVariables,
        IProviderCredentialStore credentialStore,
        ISecretInputReader secretInputReader,
        IConsole console,
        OpenAiCompatibleModelOptions options)
        : this(
            environmentVariables,
            credentialStore,
            NullLegacyProviderCredentialStore.Instance,
            secretInputReader,
            console,
            options)
    {
    }

    public ProviderCredentialResolver(
        IEnvironmentVariableReader environmentVariables,
        IProviderCredentialStore credentialStore,
        ILegacyProviderCredentialStore legacyCredentialStore,
        ISecretInputReader secretInputReader,
        IConsole console,
        OpenAiCompatibleModelOptions options)
    {
        _environmentVariables = environmentVariables ??
            throw new ArgumentNullException(nameof(environmentVariables));
        _credentialStore = credentialStore ??
            throw new ArgumentNullException(nameof(credentialStore));
        _legacyCredentialStore = legacyCredentialStore ??
            throw new ArgumentNullException(nameof(legacyCredentialStore));
        _secretInputReader = secretInputReader ??
            throw new ArgumentNullException(nameof(secretInputReader));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _scope = _options.CreateCredentialScope();
    }

    public async Task ResolveForRunAsync(
        IProviderCredentialSession credentialSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialSession);

        var environmentCredential = ValidateOptional(
            _environmentVariables.Get(_options.ApiKeyEnvironmentVariable));
        if (environmentCredential is not null)
        {
            credentialSession.Set(
                environmentCredential,
                ProviderCredentialSource.Environment);
            return;
        }

        var storedCredential = ValidateOptional(
            await _credentialStore.GetAsync(_scope, cancellationToken));
        if (storedCredential is not null)
        {
            credentialSession.Set(storedCredential, ProviderCredentialSource.Stored);
            return;
        }

        if (_scope.IsOfficialXiaomi)
        {
            var legacyCredential = ValidateOptional(
                await _legacyCredentialStore.GetLegacyAsync(cancellationToken));
            if (legacyCredential is not null)
            {
                credentialSession.Set(
                    legacyCredential,
                    ProviderCredentialSource.LegacyStored);
                return;
            }
        }

        if (_console.IsInputRedirected)
        {
            throw new CredentialResolutionException(
                $"API credential is not configured for the current model endpoint. Set {_options.ApiKeyEnvironmentVariable} or run 'agentpulse auth set'.");
        }

        await _console.Error.WriteLineAsync(
            "API credential was not found for the current model endpoint.".AsMemory(),
            cancellationToken);
        await _console.Error.WriteAsync(
            $"Enter {_options.ApiKeyEnvironmentVariable}: ".AsMemory(),
            cancellationToken);
        await _console.Error.FlushAsync(cancellationToken);

        var promptedCredential = ValidateRequired(
            await _secretInputReader.ReadAsync(cancellationToken));
        credentialSession.Set(promptedCredential, ProviderCredentialSource.Prompt);
    }

    private static string? ValidateOptional(string? value)
    {
        return value is null ? null : ValidateRequired(value);
    }

    private static string ValidateRequired(string? value)
    {
        try
        {
            return ProviderCredentialValidator.ValidateAndNormalize(value);
        }
        catch (ProviderCredentialValidationException)
        {
            throw new CredentialResolutionException(
                "The configured API credential contains invalid characters.");
        }
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
