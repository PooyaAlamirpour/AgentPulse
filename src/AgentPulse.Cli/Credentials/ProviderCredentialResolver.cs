using AgentPulse.Cli.Console;
using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Cli.Credentials;

public sealed class ProviderCredentialResolver(
    IEnvironmentVariableReader environmentVariables,
    IProviderCredentialStore credentialStore,
    ISecretInputReader secretInputReader,
    IConsole console) : IProviderCredentialResolver
{
    public const string EnvironmentVariableName = "MIMO_API_KEY";

    public async Task ResolveForRunAsync(
        IProviderCredentialSession credentialSession,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentialSession);

        var environmentCredential = Normalize(
            environmentVariables.Get(EnvironmentVariableName));
        if (environmentCredential is not null)
        {
            credentialSession.Set(
                environmentCredential,
                ProviderCredentialSource.Environment);
            return;
        }

        var storedCredential = Normalize(
            await credentialStore.GetAsync(cancellationToken));
        if (storedCredential is not null)
        {
            credentialSession.Set(storedCredential, ProviderCredentialSource.Stored);
            return;
        }

        if (console.IsInputRedirected)
        {
            throw new CredentialResolutionException(
                "Xiaomi MiMo API key is not configured. Set MIMO_API_KEY or run 'agentpulse auth set'.");
        }

        await console.Error.WriteLineAsync(
            "Xiaomi MiMo API key was not found.".AsMemory(),
            cancellationToken);
        await console.Error.WriteAsync(
            "Enter MIMO_API_KEY: ".AsMemory(),
            cancellationToken);
        await console.Error.FlushAsync(cancellationToken);

        var promptedCredential = Normalize(
            await secretInputReader.ReadAsync(cancellationToken));
        if (promptedCredential is null)
        {
            throw new CredentialResolutionException(
                "Xiaomi MiMo API key cannot be empty.");
        }

        credentialSession.Set(promptedCredential, ProviderCredentialSource.Prompt);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
