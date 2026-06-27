using AgentPulse.Application.ModelRuns;
using AgentPulse.Cli.Console;
using AgentPulse.Cli.Credentials;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPulse.Cli.Commands;

public sealed class RunCommandHandler(
    IServiceScopeFactory scopeFactory,
    IProviderCredentialResolver credentialResolver,
    IConsole console) : IRunCommandHandler
{
    public async Task<int> HandleAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;

            await services
                .GetRequiredService<IAgentPulseDatabaseInitializer>()
                .InitializeAsync(cancellationToken);

            var credentialSession = services
                .GetRequiredService<IProviderCredentialSession>();
            await credentialResolver.ResolveForRunAsync(
                credentialSession,
                cancellationToken);

            await services
                .GetRequiredService<IRunPrompt>()
                .ExecuteAsync(
                    new RunPromptRequest(prompt),
                    cancellationToken);

            return ExitCodes.Success;
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
        catch (CredentialResolutionException exception)
        {
            await WriteErrorAsync(exception.Message, cancellationToken);
            return ExitCodes.Failure;
        }
        catch (ProviderCredentialStoreException exception)
        {
            await WriteErrorAsync(exception.Message, cancellationToken);
            return ExitCodes.Failure;
        }
        catch (ModelRunException exception)
        {
            await WriteErrorAsync(exception.Message, cancellationToken);
            return exception.Code == ModelRunErrorCode.ProviderCancelled
                ? ExitCodes.Cancelled
                : ExitCodes.Failure;
        }
        catch (Exception)
        {
            await WriteErrorAsync(
                "The run failed before the model response completed.",
                cancellationToken);
            return ExitCodes.Failure;
        }
    }

    private async Task WriteErrorAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await console.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        await console.Error.FlushAsync(cancellationToken);
    }
}
