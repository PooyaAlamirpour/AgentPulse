using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
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
    public async Task<int> HandleAsync(
        RunCommandOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Prompt);

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

            var result = await services
                .GetRequiredService<IRunPrompt>()
                .ExecuteAsync(
                    new RunPromptRequest(
                        options.Prompt,
                        options.ProjectDirectory,
                        options.SessionId,
                        options.ModelOverride),
                    cancellationToken);

            await WriteSessionIdAsync(result.SessionId, cancellationToken);
            return ExitCodes.Success;
        }
        catch (SecretInputCancelledException)
        {
            await WriteCancelledAsync();
            return ExitCodes.Cancelled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteCancelledAsync();
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
        catch (ProjectContextException exception)
        {
            await WriteErrorAsync(exception.Message, cancellationToken);
            return ExitCodes.Failure;
        }
        catch (SessionRunException exception)
        {
            await WriteErrorAsync(MapSessionError(exception), cancellationToken);
            return ExitCodes.Failure;
        }
        catch (ChatModelRequestException)
        {
            await WriteErrorAsync(
                "The model request could not be built from the stored session history.",
                cancellationToken);
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

    private static string MapSessionError(SessionRunException exception)
    {
        return exception.Code switch
        {
            SessionRunErrorCode.SessionNotFound =>
                "The requested session does not exist or is no longer available.",
            SessionRunErrorCode.SessionProjectMismatch =>
                "The requested session belongs to a different project.",
            SessionRunErrorCode.SessionAlreadyRunning =>
                "The requested session already has an active run.",
            SessionRunErrorCode.InvalidUserPrompt =>
                "The prompt cannot be empty.",
            _ => "The session run state could not be updated safely.",
        };
    }

    private async Task WriteSessionIdAsync(
        AgentPulse.Domain.Sessions.SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await console.Error.WriteLineAsync(
            $"Session ID: {sessionId}".AsMemory(),
            cancellationToken);
        await console.Error.FlushAsync(cancellationToken);
    }

    private async Task WriteCancelledAsync()
    {
        await console.Error.WriteLineAsync(
            "Operation cancelled.".AsMemory(),
            CancellationToken.None);
        await console.Error.FlushAsync(CancellationToken.None);
    }

    private async Task WriteErrorAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await console.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        await console.Error.FlushAsync(cancellationToken);
    }
}
