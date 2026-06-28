using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Cli.Console;
using AgentPulse.Cli.Credentials;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Cli.Commands;

public sealed class RunCommandHandler(
    IServiceScopeFactory scopeFactory,
    IProjectContextFactory projectContextFactory,
    IProviderCredentialResolver credentialResolver,
    ICliErrorRenderer errorRenderer,
    IConsole console,
    ILogger<RunCommandHandler> logger) : IRunCommandHandler
{
    public async Task<int> HandleAsync(
        RunCommandOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Prompt);

        logger.LogDebug(
            "Run input accepted. PromptLength {PromptLength}; HasSession {HasSession}; HasModelOverride {HasModelOverride}.",
            options.Prompt.Length,
            options.SessionId is not null,
            options.ModelOverride is not null);

        try
        {
            var projectContext = await projectContextFactory.CreateForRunAsync(
                options.ProjectDirectory,
                cancellationToken);

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
                        options.ModelOverride,
                        projectContext),
                    cancellationToken);

            await WriteSessionIdAsync(result.SessionId, cancellationToken);
            logger.LogInformation(
                "Run completed. SessionId {SessionId}; UserMessageId {UserMessageId}; AssistantMessageId {AssistantMessageId}.",
                result.SessionId,
                result.UserMessageId,
                result.AssistantMessageId);
            return ExitCodes.Success;
        }
        catch (SecretInputCancelledException)
        {
            return await RenderCancellationAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await RenderCancellationAsync();
        }
        catch (Exception exception)
        {
            return await errorRenderer.RenderAsync(exception, cancellationToken);
        }
    }


    private async Task<int> RenderCancellationAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await errorRenderer.RenderCancellationAsync(cleanup.Token);
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
}
