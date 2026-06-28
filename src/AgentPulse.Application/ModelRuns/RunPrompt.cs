using System.Runtime.ExceptionServices;
using System.Text;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;

namespace AgentPulse.Application.ModelRuns;

public sealed class RunPrompt(
    IProjectContextFactory projectContextFactory,
    IPrepareSessionRun prepareSessionRun,
    IChatModelRequestBuilder requestBuilder,
    IChatModelClient chatModelClient,
    IStreamingRunPersistence persistence,
    IRunLeaseRenewalService leaseRenewalService,
    IEndSessionRun endSessionRun,
    IModelOutputSink outputSink,
    IClock clock,
    IAsyncDelay asyncDelay,
    ChatModelRunDefaults modelDefaults,
    StreamingRunOptions options) : IRunPrompt
{
    public async Task<RunPromptResult> ExecuteAsync(
        RunPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("The prompt cannot be empty.", nameof(request));
        }

        if (request.ModelOverride is not null &&
            string.IsNullOrWhiteSpace(request.ModelOverride))
        {
            throw new ArgumentException(
                "The model override cannot be empty.",
                nameof(request));
        }

        if (request.SessionId is not null &&
            request.SessionId.Value.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The session identifier cannot be empty.",
                nameof(request));
        }

        options.Validate();
        var model = request.ModelOverride?.Trim() ?? modelDefaults.Model;

        var projectContext = request.ResolvedProjectContext ??
            await projectContextFactory.CreateForRunAsync(
                request.ProjectPath,
                cancellationToken);
        var prepared = await prepareSessionRun.ExecuteAsync(
            new PrepareSessionRunRequest(
                projectContext,
                request.SessionId,
                request.Prompt,
                model),
            cancellationToken);

        return await ExecutePreparedAsync(
            projectContext,
            prepared,
            model,
            cancellationToken);
    }

    private async Task<RunPromptResult> ExecutePreparedAsync(
        ProjectContext projectContext,
        PrepareSessionRunResult prepared,
        string model,
        CancellationToken cancellationToken)
    {
        RunPromptResult? result = null;
        Exception? pendingException = null;

        try
        {
            result = await ExecutePreparedCoreAsync(
                projectContext,
                prepared,
                model,
                cancellationToken);
        }
        catch (Exception exception)
        {
            pendingException = exception;
        }
        finally
        {
            var releaseException = await TryReleaseLeaseAsync(prepared);
            if (releaseException is not null)
            {
                pendingException = pendingException is null
                    ? new ModelRunException(
                        ModelRunErrorCode.PersistenceFailure,
                        "The model run completed, but its session lock could not be released safely.",
                        releaseException)
                    : CreateCleanupFailure(pendingException, releaseException);
            }
        }

        if (pendingException is not null)
        {
            ExceptionDispatchInfo.Capture(pendingException).Throw();
        }

        return result ?? throw new ModelRunException(
            ModelRunErrorCode.UnexpectedFailure,
            "The model run ended without a result.");
    }

    private async Task<RunPromptResult> ExecutePreparedCoreAsync(
        ProjectContext projectContext,
        PrepareSessionRunResult prepared,
        string model,
        CancellationToken cancellationToken)
    {
        StringBuilder? responseText = null;
        var lastFlushedLength = 0;
        var flushCount = 0;
        var lastFlushUtc = default(DateTime);
        var messageFinalized = false;
        ModelUsage? usage = null;
        ModelFinishReason? finishReason = null;
        RunPromptResult? result = null;
        Exception? pendingException = null;
        CancellationTokenSource? streamCancellation = null;
        CancellationTokenSource? heartbeatCancellation = null;
        HeartbeatState? heartbeatState = null;
        Task? heartbeatTask = null;

        try
        {
            var responseBuffer = new StringBuilder();
            responseText = responseBuffer;
            lastFlushUtc = GetUtcNow();
            var activeStreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            streamCancellation = activeStreamCancellation;
            var activeHeartbeatCancellation = new CancellationTokenSource();
            heartbeatCancellation = activeHeartbeatCancellation;
            var activeHeartbeatState = new HeartbeatState();
            heartbeatState = activeHeartbeatState;

            var modelRequest = requestBuilder.Build(
                new ChatModelRequestBuildInput(
                    projectContext,
                    prepared.OrderedPreviousHistory,
                    prepared.UserMessage,
                    model));

            heartbeatTask = RunHeartbeatAsync(
                prepared.Session.Id,
                prepared.RunLease.LeaseId,
                activeStreamCancellation,
                activeHeartbeatCancellation.Token,
                activeHeartbeatState);

            var completedEventSeen = false;
            CancellationTokenSource? flushDelayCancellation = null;
            Task? flushDelayTask = null;

            var streamEnumerator = chatModelClient
                .StreamAsync(modelRequest, activeStreamCancellation.Token)
                .GetAsyncEnumerator(activeStreamCancellation.Token);
            Task<bool>? moveNextTask = null;
            Exception? streamException = null;

            try
            {
                moveNextTask = streamEnumerator.MoveNextAsync().AsTask();

                while (true)
                {
                    ThrowIfHeartbeatFailed(activeHeartbeatState);

                    if (flushDelayTask is null && responseBuffer.Length > lastFlushedLength)
                    {
                        var elapsed = GetUtcNow() - lastFlushUtc;
                        var remaining = options.FlushInterval - elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            remaining = TimeSpan.FromTicks(1);
                        }

                        flushDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            activeStreamCancellation.Token);
                        flushDelayTask = asyncDelay.DelayAsync(
                            remaining,
                            flushDelayCancellation.Token);
                    }

                    if (flushDelayTask is not null)
                    {
                        var winner = await Task.WhenAny(moveNextTask, flushDelayTask);
                        if (winner == flushDelayTask)
                        {
                            await flushDelayTask;
                            flushDelayCancellation!.Dispose();
                            flushDelayCancellation = null;
                            flushDelayTask = null;

                            if (responseBuffer.Length > lastFlushedLength)
                            {
                                await FlushPartialAsync(
                                    prepared.AssistantMessage.Id,
                                    responseBuffer.ToString(),
                                    activeStreamCancellation.Token);
                                lastFlushedLength = responseBuffer.Length;
                                lastFlushUtc = GetUtcNow();
                                flushCount++;
                            }

                            continue;
                        }
                    }

                    var completedMoveNextTask = moveNextTask ?? throw new InvalidOperationException(
                        "The model stream did not have an active move operation.");
                    moveNextTask = null;

                    if (!await completedMoveNextTask)
                    {
                        break;
                    }

                    var streamEvent = streamEnumerator.Current;
                    if (completedEventSeen)
                    {
                        throw new ModelRunException(
                            ModelRunErrorCode.InvalidStream,
                            "The model stream produced an event after completion.");
                    }

                    switch (streamEvent)
                    {
                        case ModelStreamEvent.TextDelta textDelta:
                            responseBuffer.Append(textDelta.Text);
                            await WriteDeltaAsync(
                                textDelta.Text,
                                activeStreamCancellation.Token);

                            if (ShouldFlush(
                                    responseBuffer.Length,
                                    lastFlushedLength,
                                    lastFlushUtc))
                            {
                                await FlushPartialAsync(
                                    prepared.AssistantMessage.Id,
                                    responseBuffer.ToString(),
                                    activeStreamCancellation.Token);
                                lastFlushedLength = responseBuffer.Length;
                                lastFlushUtc = GetUtcNow();
                                flushCount++;

                                if (flushDelayTask is not null)
                                {
                                    flushDelayCancellation!.Cancel();
                                    await ObserveExpectedCancellationAsync(flushDelayTask);
                                    flushDelayCancellation.Dispose();
                                    flushDelayCancellation = null;
                                    flushDelayTask = null;
                                }
                            }

                            break;

                        case ModelStreamEvent.Usage usageEvent:
                            usage = usageEvent.Value;
                            break;

                        case ModelStreamEvent.Failed:
                            throw new ModelProviderException(
                                ModelProviderErrorCode.Unknown,
                                GetPublicProviderMessage(ModelProviderErrorCode.Unknown),
                                responseBuffer.Length == 0
                                    ? ModelFailureStage.BeforeFirstToken
                                    : ModelFailureStage.AfterFirstToken);

                        case ModelStreamEvent.Completed completed:
                            completedEventSeen = true;
                            finishReason = completed.FinishReason;
                            break;

                        default:
                            throw new ModelRunException(
                                ModelRunErrorCode.InvalidStream,
                                $"Unsupported model stream event '{streamEvent.GetType().Name}'.");
                    }

                    moveNextTask = streamEnumerator.MoveNextAsync().AsTask();
                }
            }
            catch (Exception exception)
            {
                streamException = exception;
            }
            finally
            {
                try
                {
                    if (flushDelayTask is not null)
                    {
                        flushDelayCancellation!.Cancel();
                        await ObserveExpectedCancellationAsync(flushDelayTask);
                        flushDelayCancellation.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    streamException = CombineStreamCleanupFailure(
                        streamException,
                        exception);
                }

                streamException = await StopAndDisposeStreamAsync(
                    streamEnumerator,
                    moveNextTask,
                    activeStreamCancellation,
                    streamException);
            }

            if (streamException is not null)
            {
                ExceptionDispatchInfo.Capture(streamException).Throw();
            }

            ThrowIfHeartbeatFailed(activeHeartbeatState);
            cancellationToken.ThrowIfCancellationRequested();

            if (!completedEventSeen || finishReason is null)
            {
                throw new ModelRunException(
                    ModelRunErrorCode.InvalidStream,
                    "The model stream ended without a completion event.");
            }

            if (responseBuffer.Length == 0)
            {
                throw new ModelRunException(
                    ModelRunErrorCode.InvalidStream,
                    "The model stream completed without usable text output.");
            }

            if (finishReason == ModelFinishReason.Cancelled)
            {
                await FinalizeCancellationAsync(
                    prepared,
                    responseBuffer.ToString(),
                    model);
                messageFinalized = true;
                throw new ModelRunException(
                    ModelRunErrorCode.ProviderCancelled,
                    "The model provider cancelled the response.");
            }

            if (finishReason == ModelFinishReason.Error)
            {
                throw new ModelRunException(
                    ModelRunErrorCode.ProviderFailure,
                    "The model provider reported an error completion.");
            }

            await CompleteOutputAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await FinalizeCompletionAsync(
                prepared,
                responseBuffer.ToString(),
                new AssistantCompletionMetadata(model, finishReason.Value, usage),
                cancellationToken);
            messageFinalized = true;
            flushCount++;

            result = new RunPromptResult(
                prepared.Session.Id,
                prepared.UserMessage.Id,
                prepared.AssistantMessage.Id,
                prepared.RunLease.LeaseId,
                responseBuffer.ToString(),
                finishReason.Value,
                usage,
                flushCount);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            if (!messageFinalized)
            {
                var cleanupException = await TryFinalizeCancellationAsync(
                    prepared,
                    responseText?.ToString() ?? string.Empty,
                    model);
                messageFinalized = cleanupException is null;
                pendingException = cleanupException is null
                    ? exception
                    : CreateCleanupFailure(exception, cleanupException);
            }
            else
            {
                pendingException = exception;
            }
        }
        catch (ModelProviderOperationCanceledException)
        {
            if (!messageFinalized)
            {
                var cleanupException = await TryFinalizeCancellationAsync(
                    prepared,
                    responseText?.ToString() ?? string.Empty,
                    model);
                messageFinalized = cleanupException is null;
                var providerCancellation = new ModelRunException(
                    ModelRunErrorCode.ProviderCancelled,
                    "The model provider cancelled the request before completion.");
                pendingException = cleanupException is null
                    ? providerCancellation
                    : CreateCleanupFailure(providerCancellation, cleanupException);
            }
            else
            {
                pendingException = new ModelRunException(
                    ModelRunErrorCode.ProviderCancelled,
                    "The model provider cancelled the request before completion.");
            }
        }
        catch (OperationCanceledException)
            when (heartbeatState?.Exception is not null)
        {
            var leaseLost = new ModelRunException(
                ModelRunErrorCode.LeaseLost,
                "The run lease was lost while the model response was streaming.",
                heartbeatState!.Exception!);

            if (!messageFinalized)
            {
                var cleanupException = await TryFinalizeFailureAsync(
                    prepared,
                    responseText?.ToString() ?? string.Empty,
                    CreateFailureMetadata(leaseLost, model));
                messageFinalized = cleanupException is null;
                pendingException = cleanupException is null
                    ? leaseLost
                    : CreateCleanupFailure(leaseLost, cleanupException);
            }
            else
            {
                pendingException = leaseLost;
            }
        }
        catch (Exception exception)
        {
            var mappedException = MapRunException(exception);

            if (!messageFinalized)
            {
                var cleanupException = await TryFinalizeFailureAsync(
                    prepared,
                    responseText?.ToString() ?? string.Empty,
                    CreateFailureMetadata(exception, model));
                messageFinalized = cleanupException is null;
                pendingException = cleanupException is null
                    ? mappedException
                    : CreateCleanupFailure(mappedException, cleanupException);
            }
            else
            {
                pendingException = mappedException;
            }
        }
        finally
        {
            streamCancellation?.Cancel();
            heartbeatCancellation?.Cancel();

            try
            {
                if (heartbeatTask is not null)
                {
                    await heartbeatTask;
                }
            }
            catch (Exception exception)
            {
                pendingException = pendingException is null
                    ? MapRunException(exception)
                    : CreateCleanupFailure(pendingException, exception);
            }
            finally
            {
                streamCancellation?.Dispose();
                heartbeatCancellation?.Dispose();
            }
        }

        if (pendingException is not null)
        {
            ExceptionDispatchInfo.Capture(pendingException).Throw();
        }

        return result ?? throw new ModelRunException(
            ModelRunErrorCode.UnexpectedFailure,
            "The model run ended without a result.");
    }

    private async Task FlushPartialAsync(
        AgentPulse.Domain.Messages.MessageId assistantMessageId,
        string completeText,
        CancellationToken cancellationToken)
    {
        try
        {
            await persistence.FlushAssistantTextAsync(
                assistantMessageId,
                completeText,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModelRunException(
                ModelRunErrorCode.PersistenceFailure,
                "The partial model response could not be persisted.",
                exception);
        }
    }

    private async Task WriteDeltaAsync(
        string delta,
        CancellationToken cancellationToken)
    {
        try
        {
            await outputSink.WriteDeltaAsync(delta, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModelRunException(
                ModelRunErrorCode.OutputFailure,
                "The streamed model response could not be written to the console.",
                exception);
        }
    }

    private async Task CompleteOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            await outputSink.CompleteAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModelRunException(
                ModelRunErrorCode.OutputFailure,
                "The streamed model response could not be completed in the console.",
                exception);
        }
    }

    private async Task FinalizeCompletionAsync(
        PrepareSessionRunResult prepared,
        string completeText,
        AssistantCompletionMetadata metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(options.CleanupTimeout);
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            await persistence.CompleteAsync(
                prepared.Session.Id,
                prepared.AssistantMessage.Id,
                prepared.RunLease.LeaseId,
                completeText,
                metadata,
                cleanup.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModelRunException(
                ModelRunErrorCode.PersistenceFailure,
                "The completed model response could not be persisted.",
                exception);
        }
    }

    private async Task FinalizeCancellationAsync(
        PrepareSessionRunResult prepared,
        string completeText,
        string model)
    {
        using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
        await persistence.CancelAsync(
            prepared.Session.Id,
            prepared.AssistantMessage.Id,
            prepared.RunLease.LeaseId,
            completeText,
            model,
            cleanup.Token);
    }

    private async Task RunHeartbeatAsync(
        AgentPulse.Domain.Sessions.SessionId sessionId,
        AgentPulse.Domain.SessionRuns.RunLeaseId leaseId,
        CancellationTokenSource streamCancellation,
        CancellationToken heartbeatCancellation,
        HeartbeatState heartbeatState)
    {
        try
        {
            while (true)
            {
                await asyncDelay.DelayAsync(
                    options.LeaseRenewInterval,
                    heartbeatCancellation);
                await leaseRenewalService.RenewAsync(
                    sessionId,
                    leaseId,
                    heartbeatCancellation);
            }
        }
        catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            heartbeatState.Set(exception);
            streamCancellation.Cancel();
        }
    }

    private bool ShouldFlush(
        int currentLength,
        int lastFlushedLength,
        DateTime lastFlushUtc)
    {
        var unsavedCharacters = currentLength - lastFlushedLength;
        if (unsavedCharacters <= 0)
        {
            return false;
        }

        if (unsavedCharacters >= options.FlushCharacterThreshold)
        {
            return true;
        }

        return GetUtcNow() - lastFlushUtc >= options.FlushInterval;
    }

    private DateTime GetUtcNow()
    {
        var utcNow = clock.UtcNow;
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ModelRunException(
                ModelRunErrorCode.PersistenceFailure,
                "The configured clock returned a non-UTC timestamp.");
        }

        return utcNow;
    }

    private static void ThrowIfHeartbeatFailed(HeartbeatState state)
    {
        if (state.Exception is not null)
        {
            throw new OperationCanceledException(
                "The model stream was cancelled because the run lease could not be renewed.",
                state.Exception);
        }
    }

    private static async Task ObserveExpectedCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<Exception?> StopAndDisposeStreamAsync(
        IAsyncEnumerator<ModelStreamEvent> streamEnumerator,
        Task<bool>? activeMoveNextTask,
        CancellationTokenSource streamCancellation,
        Exception? pendingException)
    {
        if (activeMoveNextTask is { IsCompleted: false })
        {
            streamCancellation.Cancel();
        }

        if (activeMoveNextTask is not null)
        {
            try
            {
                await activeMoveNextTask;
            }
            catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                pendingException = CombineStreamCleanupFailure(
                    pendingException,
                    exception);
            }
        }

        try
        {
            await streamEnumerator.DisposeAsync();
        }
        catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            pendingException = CombineStreamCleanupFailure(
                pendingException,
                exception);
        }

        return pendingException;
    }

    private static Exception CombineStreamCleanupFailure(
        Exception? primaryException,
        Exception cleanupException)
    {
        return primaryException is null
            ? cleanupException
            : new AggregateException(primaryException, cleanupException);
    }

    private async Task<Exception?> TryFinalizeCancellationAsync(
        PrepareSessionRunResult prepared,
        string completeText,
        string model)
    {
        try
        {
            await FinalizeCancellationAsync(prepared, completeText, model);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task<Exception?> TryFinalizeFailureAsync(
        PrepareSessionRunResult prepared,
        string completeText,
        AssistantFailureMetadata metadata)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
            await persistence.FailAsync(
                prepared.Session.Id,
                prepared.AssistantMessage.Id,
                prepared.RunLease.LeaseId,
                completeText,
                metadata,
                cleanup.Token);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task<Exception?> TryReleaseLeaseAsync(PrepareSessionRunResult prepared)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
            await endSessionRun.ExecuteAsync(
                prepared.Session.Id,
                prepared.RunLease.LeaseId,
                cleanup.Token);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static AssistantFailureMetadata CreateFailureMetadata(
        Exception exception,
        string model)
    {
        return exception switch
        {
            ModelProviderException providerException => new AssistantFailureMetadata(
                model,
                GetPublicProviderMessage(providerException.Code),
                providerException.Code.ToString(),
                providerException.FailureStage.ToString(),
                providerException.HttpStatusCode is null
                    ? null
                    : (int)providerException.HttpStatusCode.Value),
            ModelProviderOperationCanceledException providerCancellation =>
                new AssistantFailureMetadata(
                    model,
                    "The model provider cancelled the request before completion.",
                    providerCancellation.Code.ToString(),
                    providerCancellation.FailureStage.ToString()),
            ModelRunException runException => new AssistantFailureMetadata(
                model,
                GetPublicRunFailureMessage(runException.Code),
                runException.Code.ToString()),
            ChatModelRequestException => new AssistantFailureMetadata(
                model,
                "The model request could not be built from the stored session history.",
                "Validation"),
            SessionRunException => new AssistantFailureMetadata(
                model,
                "The model run state could not be persisted safely.",
                "Persistence"),
            _ => new AssistantFailureMetadata(
                model,
                "The model run failed before completion.",
                "Unexpected"),
        };
    }

    private static Exception MapRunException(Exception exception)
    {
        return exception switch
        {
            ModelRunException => exception,
            ModelProviderException providerException => new ModelRunException(
                ModelRunErrorCode.ProviderFailure,
                GetPublicProviderMessage(providerException.Code),
                providerException.Code),
            ModelProviderOperationCanceledException => new ModelRunException(
                ModelRunErrorCode.ProviderCancelled,
                "The model provider cancelled the request before completion."),
            ChatModelRequestException requestException => new ModelRunException(
                ModelRunErrorCode.ValidationFailure,
                "The model request could not be built from the stored session history.",
                requestException),
            SessionRunException persistenceException => new ModelRunException(
                ModelRunErrorCode.PersistenceFailure,
                "The model run state could not be persisted safely.",
                persistenceException),
            _ => new ModelRunException(
                ModelRunErrorCode.UnexpectedFailure,
                "The model run failed.",
                exception),
        };
    }

    private static ModelRunException CreateCleanupFailure(
        Exception primaryException,
        Exception cleanupException)
    {
        return new ModelRunException(
            ModelRunErrorCode.PersistenceFailure,
            "The model run failed and its final state could not be persisted safely.",
            new AggregateException(primaryException, cleanupException));
    }

    private static string GetPublicRunFailureMessage(ModelRunErrorCode code)
    {
        return code switch
        {
            ModelRunErrorCode.InvalidStream =>
                "The model provider returned an invalid streaming response.",
            ModelRunErrorCode.LeaseLost =>
                "The session run lock was lost while streaming.",
            ModelRunErrorCode.PersistenceFailure =>
                "The model response could not be persisted safely.",
            ModelRunErrorCode.OutputFailure =>
                "The model response could not be rendered safely.",
            ModelRunErrorCode.ValidationFailure =>
                "The model request could not be built from the stored session history.",
            ModelRunErrorCode.ProviderCancelled =>
                "The model provider cancelled the request before completion.",
            _ => "The model run failed before completion.",
        };
    }

    private static string GetPublicProviderMessage(ModelProviderErrorCode code)
    {
        return code switch
        {
            ModelProviderErrorCode.Authentication =>
                "The model provider rejected the API credential.",
            ModelProviderErrorCode.PermissionDenied =>
                "The model provider denied access to the requested resource.",
            ModelProviderErrorCode.RateLimited =>
                "The model provider rate limit was exceeded.",
            ModelProviderErrorCode.InvalidRequest =>
                "The model provider rejected the request.",
            ModelProviderErrorCode.Unavailable =>
                "The model provider is temporarily unavailable.",
            ModelProviderErrorCode.Timeout =>
                "The model provider request timed out.",
            ModelProviderErrorCode.Protocol or ModelProviderErrorCode.InvalidResponse =>
                "The model provider returned an invalid response.",
            ModelProviderErrorCode.Cancelled =>
                "The model provider cancelled the request before completion.",
            ModelProviderErrorCode.UnsupportedFeature =>
                "The model provider does not support the requested operation.",
            _ => "The model provider request failed.",
        };
    }

    private sealed class HeartbeatState
    {
        private readonly object _gate = new();
        private Exception? _exception;

        public Exception? Exception
        {
            get
            {
                lock (_gate)
                {
                    return _exception;
                }
            }
        }

        public void Set(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            lock (_gate)
            {
                _exception ??= exception;
            }
        }
    }
}
