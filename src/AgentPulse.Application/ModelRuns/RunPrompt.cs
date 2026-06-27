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
    IModelOutputSink outputSink,
    IClock clock,
    IAsyncDelay asyncDelay,
    StreamingRunOptions options) : IRunPrompt
{
    private const int MaximumFailureReasonLength = 1024;

    public async Task<RunPromptResult> ExecuteAsync(
        RunPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("The prompt cannot be empty.", nameof(request));
        }

        options.Validate();

        var projectContext = await projectContextFactory.CreateAsync(
            request.ProjectPath,
            cancellationToken);
        var prepared = await prepareSessionRun.ExecuteAsync(
            new PrepareSessionRunRequest(projectContext, request.SessionId, request.Prompt),
            cancellationToken);

        var responseText = new StringBuilder();
        var lastFlushedLength = 0;
        var flushCount = 0;
        var lastFlushUtc = GetUtcNow();
        var finalized = false;
        ModelUsage? usage = null;
        ModelFinishReason? finishReason = null;

        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var heartbeatCancellation = new CancellationTokenSource();
        var heartbeatState = new HeartbeatState();
        Task? heartbeatTask = null;

        try
        {
            var modelRequest = requestBuilder.Build(
                new ChatModelRequestBuildInput(
                    projectContext,
                    prepared.OrderedPreviousHistory,
                    prepared.UserMessage));

            heartbeatTask = RunHeartbeatAsync(
                prepared.Session.Id,
                prepared.RunLease.LeaseId,
                streamCancellation,
                heartbeatCancellation.Token,
                heartbeatState);

            var completedEventSeen = false;
            CancellationTokenSource? flushDelayCancellation = null;
            Task? flushDelayTask = null;

            await using var streamEnumerator = chatModelClient
                .StreamAsync(modelRequest, streamCancellation.Token)
                .GetAsyncEnumerator(streamCancellation.Token);
            var moveNextTask = streamEnumerator.MoveNextAsync().AsTask();

            try
            {
                while (true)
                {
                    ThrowIfHeartbeatFailed(heartbeatState);

                    if (flushDelayTask is null && responseText.Length > lastFlushedLength)
                    {
                        var elapsed = GetUtcNow() - lastFlushUtc;
                        var remaining = options.FlushInterval - elapsed;
                        if (remaining <= TimeSpan.Zero)
                        {
                            remaining = TimeSpan.FromTicks(1);
                        }

                        flushDelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            streamCancellation.Token);
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

                            if (responseText.Length > lastFlushedLength)
                            {
                                await persistence.FlushAssistantTextAsync(
                                    prepared.AssistantMessage.Id,
                                    responseText.ToString(),
                                    streamCancellation.Token);
                                lastFlushedLength = responseText.Length;
                                lastFlushUtc = GetUtcNow();
                                flushCount++;
                            }

                            continue;
                        }
                    }

                    if (!await moveNextTask)
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
                            responseText.Append(textDelta.Text);
                            await outputSink.WriteDeltaAsync(
                                textDelta.Text,
                                streamCancellation.Token);

                            if (ShouldFlush(
                                    responseText.Length,
                                    lastFlushedLength,
                                    lastFlushUtc))
                            {
                                await persistence.FlushAssistantTextAsync(
                                    prepared.AssistantMessage.Id,
                                    responseText.ToString(),
                                    streamCancellation.Token);
                                lastFlushedLength = responseText.Length;
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

                        case ModelStreamEvent.Failed failed:
                            throw new ModelProviderException(
                                ModelProviderErrorCode.Unknown,
                                failed.ErrorMessage);

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
            finally
            {
                if (flushDelayTask is not null)
                {
                    flushDelayCancellation!.Cancel();
                    await ObserveExpectedCancellationAsync(flushDelayTask);
                    flushDelayCancellation.Dispose();
                }
            }

            ThrowIfHeartbeatFailed(heartbeatState);

            if (!completedEventSeen || finishReason is null)
            {
                throw new ModelRunException(
                    ModelRunErrorCode.InvalidStream,
                    "The model stream ended without a completion event.");
            }

            if (responseText.Length == 0)
            {
                throw new ModelRunException(
                    ModelRunErrorCode.InvalidStream,
                    "The model stream completed without usable text output.");
            }

            if (finishReason == ModelFinishReason.Cancelled)
            {
                await persistence.CancelAsync(
                    prepared.Session.Id,
                    prepared.AssistantMessage.Id,
                    prepared.RunLease.LeaseId,
                    responseText.ToString(),
                    CancellationToken.None);
                finalized = true;
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

            await persistence.CompleteAsync(
                prepared.Session.Id,
                prepared.AssistantMessage.Id,
                prepared.RunLease.LeaseId,
                responseText.ToString(),
                CancellationToken.None);
            finalized = true;
            flushCount++;

            await outputSink.CompleteAsync(CancellationToken.None);

            return new RunPromptResult(
                prepared.Session.Id,
                prepared.UserMessage.Id,
                prepared.AssistantMessage.Id,
                prepared.RunLease.LeaseId,
                responseText.ToString(),
                finishReason.Value,
                usage,
                flushCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!finalized)
            {
                await FinalizeCancellationAsync(prepared, responseText.ToString());
                finalized = true;
            }

            throw;
        }
        catch (OperationCanceledException) when (heartbeatState.Exception is not null)
        {
            var heartbeatException = heartbeatState.Exception!;

            if (!finalized)
            {
                await FinalizeFailureAsync(
                    prepared,
                    responseText.ToString(),
                    "The run lease was lost while the model response was streaming.");
                finalized = true;
            }

            throw new ModelRunException(
                ModelRunErrorCode.LeaseLost,
                "The run lease was lost while the model response was streaming.",
                heartbeatException);
        }
        catch (Exception exception)
        {
            if (!finalized)
            {
                var cleanupException = await TryFinalizeFailureAsync(
                    prepared,
                    responseText.ToString(),
                    ToSafeFailureReason(exception));
                finalized = cleanupException is null;

                if (cleanupException is not null)
                {
                    throw new ModelRunException(
                        ModelRunErrorCode.PersistenceFailure,
                        "The model run failed and its final state could not be persisted safely.",
                        new AggregateException(exception, cleanupException));
                }
            }

            if (exception is ModelRunException)
            {
                throw;
            }

            if (exception is ModelProviderException providerException)
            {
                throw new ModelRunException(
                    ModelRunErrorCode.ProviderFailure,
                    providerException.Message,
                    providerException);
            }

            throw new ModelRunException(
                ModelRunErrorCode.ProviderFailure,
                "The model run failed.",
                exception);
        }
        finally
        {
            streamCancellation.Cancel();
            heartbeatCancellation.Cancel();

            if (heartbeatTask is not null)
            {
                await heartbeatTask;
            }
        }
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

    private async Task FinalizeCancellationAsync(
        PrepareSessionRunResult prepared,
        string completeText)
    {
        await persistence.CancelAsync(
            prepared.Session.Id,
            prepared.AssistantMessage.Id,
            prepared.RunLease.LeaseId,
            completeText,
            CancellationToken.None);
    }

    private async Task FinalizeFailureAsync(
        PrepareSessionRunResult prepared,
        string completeText,
        string failureReason)
    {
        await persistence.FailAsync(
            prepared.Session.Id,
            prepared.AssistantMessage.Id,
            prepared.RunLease.LeaseId,
            completeText,
            failureReason,
            CancellationToken.None);
    }

    private async Task<Exception?> TryFinalizeFailureAsync(
        PrepareSessionRunResult prepared,
        string completeText,
        string failureReason)
    {
        try
        {
            await FinalizeFailureAsync(prepared, completeText, failureReason);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static string ToSafeFailureReason(Exception exception)
    {
        var message = exception switch
        {
            ModelProviderException providerException => providerException.Message,
            ModelRunException runException => runException.Message,
            _ => "The model run failed before completion.",
        };

        message = message.Trim();
        return message.Length <= MaximumFailureReasonLength
            ? message
            : message[..MaximumFailureReasonLength];
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
