using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Application.ModelRuns;

public sealed class ToolCallingRunPrompt(
    IProjectContextFactory projectContextFactory,
    IPrepareSessionRun prepareSessionRun,
    IChatModelRequestBuilder requestBuilder,
    IAgentLoop agentLoop,
    IAgentToolTurnPersistence toolTurnPersistence,
    IStreamingRunPersistence streamingPersistence,
    IRunLeaseRenewalService leaseRenewalService,
    IEndSessionRun endSessionRun,
    IModelOutputSink outputSink,
    ChatModelRunDefaults modelDefaults,
    StreamingRunOptions streamingOptions,
    ILogger<ToolCallingRunPrompt> logger) : IRunPrompt
{
    public async Task<RunPromptResult> ExecuteAsync(
        RunPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var model = request.ModelOverride?.Trim() ?? modelDefaults.Model;
        var projectContext = request.ResolvedProjectContext ??
            await projectContextFactory.CreateForRunAsync(request.ProjectPath, cancellationToken);
        var prepared = await prepareSessionRun.ExecuteAsync(
            new PrepareSessionRunRequest(
                projectContext,
                request.SessionId,
                request.Prompt,
                model),
            cancellationToken);
        var observer = new PersistenceObserver(
            prepared.Session.Id,
            prepared.AssistantMessage.Id,
            prepared.RunLease.LeaseId,
            model,
            toolTurnPersistence,
            streamingPersistence,
            leaseRenewalService);

        Exception? pendingException = null;
        RunPromptResult? runResult = null;
        try
        {
            var initialRequest = requestBuilder.Build(new ChatModelRequestBuildInput(
                projectContext,
                prepared.OrderedPreviousHistory,
                prepared.UserMessage,
                model));
            var loopResult = await agentLoop.ExecuteAsync(
                new AgentLoopRequest(initialRequest.Messages, projectContext.ProjectRoot, model),
                observer,
                cancellationToken);

            await outputSink.WriteDeltaAsync(loopResult.Text, cancellationToken);
            await outputSink.CompleteAsync(cancellationToken);
            runResult = new RunPromptResult(
                prepared.Session.Id,
                prepared.UserMessage.Id,
                observer.CurrentAssistantMessageId,
                prepared.RunLease.LeaseId,
                loopResult.Text,
                loopResult.FinishReason,
                loopResult.Usage,
                FlushCount: 0);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            pendingException = exception;
            if (!observer.IsToolTurnOpen)
            {
                await TryCancelAsync(observer, model);
            }
            else
            {
                logger.LogInformation("User cancellation preserved the persisted tool turn for session {SessionId}.", observer.SessionId);
            }
        }
        catch (Exception exception)
        {
            pendingException = exception;
            if (!observer.IsToolTurnOpen)
            {
                await TryFailAsync(observer, model, exception);
            }
            else
            {
                logger.LogError(exception, "Agent failure preserved the persisted tool turn for session {SessionId}.", observer.SessionId);
            }
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(streamingOptions.CleanupTimeout);
                await endSessionRun.ExecuteAsync(
                    prepared.Session.Id,
                    prepared.RunLease.LeaseId,
                    cleanup.Token);
            }
            catch (Exception cleanupException)
            {
                pendingException = pendingException is null
                    ? cleanupException
                    : new AggregateException(pendingException, cleanupException);
            }
        }

        if (pendingException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(MapException(pendingException))
                .Throw();
        }

        return runResult ?? throw new ModelRunException(
            ModelRunErrorCode.UnexpectedFailure,
            "The agent run ended without a result.");
    }

    private async Task TryCancelAsync(PersistenceObserver observer, string model)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(streamingOptions.CleanupTimeout);
            await streamingPersistence.CancelAsync(
                observer.SessionId,
                observer.CurrentAssistantMessageId,
                observer.LeaseId,
                string.Empty,
                model,
                cleanup.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist agent cancellation state.");
        }
    }

    private async Task TryFailAsync(PersistenceObserver observer, string model, Exception exception)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(streamingOptions.CleanupTimeout);
            await streamingPersistence.FailAsync(
                observer.SessionId,
                observer.CurrentAssistantMessageId,
                observer.LeaseId,
                string.Empty,
                new AssistantFailureMetadata(
                    model,
                    GetPublicFailureMessage(exception),
                    GetFailureKind(exception)),
                cleanup.Token);
        }
        catch (Exception persistenceException)
        {
            logger.LogError(persistenceException, "Failed to persist agent failure state.");
        }
    }

    private static void ValidateRequest(RunPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("The prompt cannot be empty.", nameof(request));
        }

        if (request.ModelOverride is not null && string.IsNullOrWhiteSpace(request.ModelOverride))
        {
            throw new ArgumentException("The model override cannot be empty.", nameof(request));
        }
    }

    private static Exception MapException(Exception exception)
    {
        if (exception is OperationCanceledException or ModelRunException)
        {
            return exception;
        }

        if (exception is AgentLoopException loopException)
        {
            if (loopException.Code == AgentLoopErrorCode.ProviderFailure &&
                loopException.InnerException is ModelProviderException providerException)
            {
                return new ModelRunException(
                    ModelRunErrorCode.ProviderFailure,
                    GetPublicFailureMessage(exception),
                    providerException.Code);
            }

            var code = loopException.Code switch
            {
                AgentLoopErrorCode.MaxIterationsReached =>
                    ModelRunErrorCode.MaxToolIterationsReached,
                AgentLoopErrorCode.InvalidResponse =>
                    ModelRunErrorCode.InvalidAgentResponse,
                AgentLoopErrorCode.ProviderFailure =>
                    ModelRunErrorCode.ProviderFailure,
                _ => ModelRunErrorCode.UnexpectedFailure,
            };
            return new ModelRunException(code, GetPublicFailureMessage(exception), exception);
        }

        return new ModelRunException(
            ModelRunErrorCode.UnexpectedFailure,
            GetPublicFailureMessage(exception),
            exception);
    }

    private static string GetFailureKind(Exception exception) => exception switch
    {
        AgentLoopException loop => loop.Code.ToString(),
        ModelProviderException provider => provider.Code.ToString(),
        ChatModelRequestException => "Validation",
        SessionRunException => "Persistence",
        _ => "Unexpected",
    };

    private static string GetPublicFailureMessage(Exception exception) => exception switch
    {
        AgentLoopException { Code: AgentLoopErrorCode.MaxIterationsReached } =>
            "The agent reached the configured maximum number of tool iterations.",
        AgentLoopException { Code: AgentLoopErrorCode.InvalidResponse } =>
            "The model returned an invalid agent response.",
        AgentLoopException => "The agent loop failed before completion.",
        ChatModelRequestException =>
            "The model request could not be built from the stored session history.",
        SessionRunException => "The agent run state could not be persisted safely.",
        _ => "The agent run failed before completion.",
    };

    private sealed class PersistenceObserver(
        AgentPulse.Domain.Sessions.SessionId sessionId,
        MessageId assistantMessageId,
        AgentPulse.Domain.SessionRuns.RunLeaseId leaseId,
        string model,
        IAgentToolTurnPersistence toolTurnPersistence,
        IStreamingRunPersistence streamingPersistence,
        IRunLeaseRenewalService leaseRenewalService) : IAgentLoopObserver
    {
        private bool _toolTurnOpen;

        public AgentPulse.Domain.Sessions.SessionId SessionId { get; } = sessionId;
        public AgentPulse.Domain.SessionRuns.RunLeaseId LeaseId { get; } = leaseId;
        public MessageId CurrentAssistantMessageId { get; private set; } = assistantMessageId;
        public bool IsToolTurnOpen => _toolTurnOpen;

        public async Task RecordAssistantResponseAsync(
            ChatModelResponse response,
            int iteration,
            CancellationToken cancellationToken)
        {
            await leaseRenewalService.RenewAsync(SessionId, LeaseId, cancellationToken);
            if (response.ToolCalls.Count > 0)
            {
                await toolTurnPersistence.SaveAssistantToolCallsAsync(
                    SessionId, CurrentAssistantMessageId, LeaseId, model, response, cancellationToken);
                _toolTurnOpen = true;
                return;
            }

            await streamingPersistence.CompleteAsync(
                SessionId,
                CurrentAssistantMessageId,
                LeaseId,
                response.Text ?? string.Empty,
                new AssistantCompletionMetadata(model, response.FinishReason, response.Usage),
                cancellationToken);
        }

        public Task RecordToolResultAsync(
            AgentLoopToolExecution result,
            int iteration,
            CancellationToken cancellationToken)
        {
            if (!_toolTurnOpen)
            {
                throw new InvalidOperationException("A tool result was received without a persisted assistant tool-call message.");
            }

            return toolTurnPersistence.SaveToolResultAsync(
                SessionId, CurrentAssistantMessageId, LeaseId, result, cancellationToken);
        }

        public async Task CompleteToolTurnAsync(int iteration, CancellationToken cancellationToken)
        {
            if (!_toolTurnOpen)
            {
                throw new InvalidOperationException("A tool turn completion was received without a persisted assistant tool-call message.");
            }

            CurrentAssistantMessageId = await toolTurnPersistence.StartNextAssistantMessageAsync(
                SessionId, LeaseId, model, cancellationToken);
            _toolTurnOpen = false;
        }
    }
}
