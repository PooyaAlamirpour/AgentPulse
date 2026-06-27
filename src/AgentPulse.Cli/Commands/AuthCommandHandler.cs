using AgentPulse.Cli.Console;
using AgentPulse.Cli.Credentials;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Cli.Commands;

public sealed class AuthCommandHandler : IAuthCommandHandler
{
    private readonly IEnvironmentVariableReader _environmentVariables;
    private readonly IProviderCredentialStore _credentialStore;
    private readonly ILegacyProviderCredentialStore _legacyCredentialStore;
    private readonly ISecretInputReader _secretInputReader;
    private readonly IConsole _console;
    private readonly OpenAiCompatibleModelOptions _options;
    private readonly ProviderCredentialScope _scope;

    public AuthCommandHandler(
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

    public AuthCommandHandler(
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

    public AuthCommandHandler(
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

    public async Task<int> HandleAsync(
        string subcommand,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return subcommand switch
            {
                "set" => await SetAsync(cancellationToken),
                "status" => await StatusAsync(cancellationToken),
                "clear" => await ClearAsync(cancellationToken),
                _ => await UnknownAsync(subcommand, cancellationToken),
            };
        }
        catch (SecretInputCancelledException)
        {
            await _console.Error.WriteLineAsync(
                "Operation cancelled.".AsMemory(),
                CancellationToken.None);
            await _console.Error.FlushAsync(CancellationToken.None);
            return ExitCodes.Cancelled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _console.Error.WriteLineAsync(
                "Operation cancelled.".AsMemory(),
                CancellationToken.None);
            await _console.Error.FlushAsync(CancellationToken.None);
            return ExitCodes.Cancelled;
        }
        catch (ProviderCredentialStoreException exception)
        {
            await _console.Error.WriteLineAsync(
                exception.Message.AsMemory(),
                cancellationToken);
            await _console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> SetAsync(CancellationToken cancellationToken)
    {
        if (_console.IsInputRedirected)
        {
            await _console.Error.WriteLineAsync(
                "The API credential must be entered from an interactive terminal.".AsMemory(),
                cancellationToken);
            await _console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }

        await _console.Error.WriteAsync(
            $"Enter {_options.ApiKeyEnvironmentVariable}: ".AsMemory(),
            cancellationToken);
        await _console.Error.FlushAsync(cancellationToken);

        var credential = (await _secretInputReader.ReadAsync(cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(credential))
        {
            await _console.Error.WriteLineAsync(
                "The API credential cannot be empty.".AsMemory(),
                cancellationToken);
            await _console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }

        await _credentialStore.SaveAsync(_scope, credential, cancellationToken);
        await _console.Out.WriteLineAsync(
            "API credential was stored securely for the current model endpoint.".AsMemory(),
            cancellationToken);
        await _console.Out.FlushAsync(cancellationToken);
        return ExitCodes.Success;
    }

    private async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var environmentCredential = _environmentVariables.Get(
            _options.ApiKeyEnvironmentVariable);

        string message;
        if (!string.IsNullOrWhiteSpace(environmentCredential))
        {
            message = "Configured API key environment variable is available.";
        }
        else
        {
            var scopedCredentialExists = await _credentialStore.ExistsAsync(
                _scope,
                cancellationToken);
            var legacyCredentialExists = !scopedCredentialExists &&
                                         _scope.IsOfficialXiaomi &&
                                         await _legacyCredentialStore.LegacyExistsAsync(
                                             cancellationToken);
            message = scopedCredentialExists || legacyCredentialExists
                ? "API credential is configured for the current model endpoint."
                : "API credential is not configured for the current model endpoint.";
        }

        await _console.Out.WriteLineAsync(message.AsMemory(), cancellationToken);
        await _console.Out.FlushAsync(cancellationToken);
        return ExitCodes.Success;
    }

    private async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        await _credentialStore.DeleteAsync(_scope, cancellationToken);
        if (_scope.IsOfficialXiaomi)
        {
            await _legacyCredentialStore.DeleteLegacyAsync(cancellationToken);
        }

        await _console.Out.WriteLineAsync(
            "Stored API credential was cleared for the current model endpoint.".AsMemory(),
            cancellationToken);
        await _console.Out.FlushAsync(cancellationToken);
        return ExitCodes.Success;
    }

    private async Task<int> UnknownAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        await _console.Error.WriteLineAsync(
            $"Unknown auth command: {subcommand}".AsMemory(),
            cancellationToken);
        await _console.Error.FlushAsync(cancellationToken);
        return ExitCodes.Failure;
    }

    private sealed class NullLegacyProviderCredentialStore : ILegacyProviderCredentialStore
    {
        public static NullLegacyProviderCredentialStore Instance { get; } = new();

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
