using AgentPulse.Cli.Console;
using AgentPulse.Cli.Credentials;
using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Cli.Commands;

public sealed class AuthCommandHandler(
    IEnvironmentVariableReader environmentVariables,
    IProviderCredentialStore credentialStore,
    ISecretInputReader secretInputReader,
    IConsole console) : IAuthCommandHandler
{
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
            await console.Error.WriteLineAsync(
                "Operation cancelled.".AsMemory(),
                CancellationToken.None);
            await console.Error.FlushAsync(CancellationToken.None);
            return ExitCodes.Cancelled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await console.Error.WriteLineAsync(
                "Operation cancelled.".AsMemory(),
                CancellationToken.None);
            await console.Error.FlushAsync(CancellationToken.None);
            return ExitCodes.Cancelled;
        }
        catch (ProviderCredentialStoreException exception)
        {
            await console.Error.WriteLineAsync(
                exception.Message.AsMemory(),
                cancellationToken);
            await console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }
    }

    private async Task<int> SetAsync(CancellationToken cancellationToken)
    {
        if (console.IsInputRedirected)
        {
            await console.Error.WriteLineAsync(
                "The API key must be entered from an interactive terminal.".AsMemory(),
                cancellationToken);
            await console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }

        await console.Error.WriteAsync(
            "Enter MIMO_API_KEY: ".AsMemory(),
            cancellationToken);
        await console.Error.FlushAsync(cancellationToken);

        var credential = (await secretInputReader.ReadAsync(cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(credential))
        {
            await console.Error.WriteLineAsync(
                "Xiaomi MiMo API key cannot be empty.".AsMemory(),
                cancellationToken);
            await console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }

        await credentialStore.SaveAsync(credential, cancellationToken);
        await console.Out.WriteLineAsync(
            "API credential was stored securely.".AsMemory(),
            cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
        return ExitCodes.Success;
    }

    private async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        var environmentCredential = environmentVariables.Get(
            ProviderCredentialResolver.EnvironmentVariableName);

        var message = !string.IsNullOrWhiteSpace(environmentCredential)
            ? "MIMO_API_KEY environment variable is configured."
            : await credentialStore.ExistsAsync(cancellationToken)
                ? "API credential is configured."
                : "API credential is not configured.";

        await console.Out.WriteLineAsync(message.AsMemory(), cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
        return ExitCodes.Success;
    }

    private async Task<int> ClearAsync(CancellationToken cancellationToken)
    {
        await credentialStore.DeleteAsync(cancellationToken);
        await console.Out.WriteLineAsync(
            "Stored API credential was cleared.".AsMemory(),
            cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
        return ExitCodes.Success;
    }

    private async Task<int> UnknownAsync(
        string subcommand,
        CancellationToken cancellationToken)
    {
        await console.Error.WriteLineAsync(
            $"Unknown auth command: {subcommand}".AsMemory(),
            cancellationToken);
        await console.Error.FlushAsync(cancellationToken);
        return ExitCodes.Failure;
    }
}
