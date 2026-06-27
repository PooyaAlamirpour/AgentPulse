using System.Runtime.CompilerServices;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Tests.ModelRuns;

public sealed class RunPromptTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Successful_stream_renders_separate_deltas_and_persists_exact_text()
    {
        var fixture = new Fixture();
        fixture.Model.Events =
        [
            new ModelStreamEvent.TextDelta("Hel"),
            new ModelStreamEvent.TextDelta("lo"),
            new ModelStreamEvent.Usage(new ModelUsage(2, 1, 3)),
            new ModelStreamEvent.Completed(ModelFinishReason.Stop),
        ];

        var result = await fixture.RunAsync();

        Assert.Equal(["Hel", "lo"], fixture.Output.Deltas);
        Assert.Equal("Hello", result.Text);
        Assert.Equal("Hello", fixture.Persistence.CompletedText);
        Assert.Equal(ModelFinishReason.Stop, result.FinishReason);
        Assert.Equal(new ModelUsage(2, 1, 3), result.Usage);
        Assert.True(fixture.Output.Completed);
        Assert.True(fixture.Prepare.WasCalledBeforeModel);
        Assert.Equal(1, fixture.Persistence.CompleteCalls);
        Assert.Equal(0, fixture.Persistence.FailCalls);
        Assert.Equal(0, fixture.Persistence.CancelCalls);
    }

    [Fact]
    public async Task Character_threshold_flushes_full_text_without_flushing_every_delta()
    {
        var fixture = new Fixture(new StreamingRunOptions
        {
            FlushInterval = TimeSpan.FromHours(1),
            FlushCharacterThreshold = 4,
            LeaseRenewInterval = TimeSpan.FromMinutes(1),
        });
        fixture.Model.Events =
        [
            new ModelStreamEvent.TextDelta("He"),
            new ModelStreamEvent.TextDelta("ll"),
            new ModelStreamEvent.TextDelta("o"),
            new ModelStreamEvent.Completed(ModelFinishReason.Stop),
        ];

        var result = await fixture.RunAsync();

        Assert.Equal(["Hell"], fixture.Persistence.IntermediateTexts);
        Assert.Equal("Hello", fixture.Persistence.CompletedText);
        Assert.Equal(2, result.FlushCount);
        Assert.True(result.FlushCount < fixture.Output.Deltas.Count);
    }

    [Fact]
    public async Task Time_threshold_uses_testable_clock_without_real_delay()
    {
        var fixture = new Fixture(new StreamingRunOptions
        {
            FlushInterval = TimeSpan.FromSeconds(1),
            FlushCharacterThreshold = 100,
            LeaseRenewInterval = TimeSpan.FromMinutes(1),
        });
        fixture.Model.OnBeforeEvent = index =>
        {
            if (index == 1)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            }
        };
        fixture.Model.Events =
        [
            new ModelStreamEvent.TextDelta("A"),
            new ModelStreamEvent.TextDelta("B"),
            new ModelStreamEvent.Completed(ModelFinishReason.Stop),
        ];

        await fixture.RunAsync();

        Assert.Equal(["AB"], fixture.Persistence.IntermediateTexts);
        Assert.Equal("AB", fixture.Persistence.CompletedText);
    }

    [Fact]
    public async Task Periodic_flush_occurs_while_waiting_for_the_next_delta()
    {
        var delay = new PeriodicFlushDelay();
        var fixture = new Fixture(
            new StreamingRunOptions
            {
                FlushInterval = TimeSpan.FromSeconds(1),
                FlushCharacterThreshold = 100,
                LeaseRenewInterval = TimeSpan.FromHours(1),
            },
            delay);
        fixture.Model.StreamFactory = token => StreamAcrossPeriodicFlush(
            delay,
            fixture.Persistence,
            token);

        var result = await fixture.RunAsync();

        Assert.Equal(["Hel"], fixture.Persistence.IntermediateTexts);
        Assert.Equal("Hello", result.Text);
        Assert.Equal(2, result.FlushCount);
    }

    [Fact]
    public async Task Provider_failure_after_partial_text_preserves_text_and_fails_run()
    {
        var fixture = new Fixture();
        fixture.Model.StreamFactory = static cancellationToken =>
            PartialThenFail(cancellationToken);

        var exception = await Assert.ThrowsAsync<ModelRunException>(() => fixture.RunAsync());

        Assert.Equal(ModelRunErrorCode.ProviderFailure, exception.Code);
        Assert.Equal(["Hel"], fixture.Output.Deltas);
        Assert.Equal("Hel", fixture.Persistence.FailedText);
        Assert.Equal(1, fixture.Persistence.FailCalls);
        Assert.Equal(0, fixture.Persistence.CancelCalls);
        Assert.False(fixture.Output.Completed);
    }

    [Fact]
    public async Task User_cancellation_after_partial_text_preserves_text_and_cancels_run()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Model.StreamFactory = token => PartialThenCancel(cancellation, token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.RunAsync(cancellation.Token));

        Assert.Equal(["Hel"], fixture.Output.Deltas);
        Assert.Equal("Hel", fixture.Persistence.CancelledText);
        Assert.Equal(1, fixture.Persistence.CancelCalls);
        Assert.Equal(0, fixture.Persistence.FailCalls);
    }

    [Fact]
    public async Task Cancellation_before_first_token_keeps_empty_assistant_part()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new Fixture();
        fixture.Model.StreamFactory = token => CancelBeforeText(cancellation, token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.RunAsync(cancellation.Token));

        Assert.Empty(fixture.Output.Deltas);
        Assert.Equal(string.Empty, fixture.Persistence.CancelledText);
        Assert.Equal(1, fixture.Persistence.CancelCalls);
    }

    [Fact]
    public async Task Intermediate_flush_failure_attempts_safe_final_failure_with_partial_text()
    {
        var fixture = new Fixture(new StreamingRunOptions
        {
            FlushInterval = TimeSpan.FromHours(1),
            FlushCharacterThreshold = 1,
            LeaseRenewInterval = TimeSpan.FromMinutes(1),
        });
        fixture.Persistence.ThrowOnIntermediateFlush = true;
        fixture.Model.Events =
        [
            new ModelStreamEvent.TextDelta("Hel"),
            new ModelStreamEvent.Completed(ModelFinishReason.Stop),
        ];

        var exception = await Assert.ThrowsAsync<ModelRunException>(() => fixture.RunAsync());

        Assert.Equal(ModelRunErrorCode.ProviderFailure, exception.Code);
        Assert.Equal("Hel", fixture.Persistence.FailedText);
        Assert.Equal(1, fixture.Persistence.FailCalls);
    }

    [Fact]
    public async Task Final_flush_failure_is_reported_as_persistence_failure()
    {
        var fixture = new Fixture();
        fixture.Persistence.ThrowOnComplete = true;
        fixture.Persistence.ThrowOnFail = true;
        fixture.Model.Events =
        [
            new ModelStreamEvent.TextDelta("Hello"),
            new ModelStreamEvent.Completed(ModelFinishReason.Stop),
        ];

        var exception = await Assert.ThrowsAsync<ModelRunException>(() => fixture.RunAsync());

        Assert.Equal(ModelRunErrorCode.PersistenceFailure, exception.Code);
        Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal("Hello", fixture.Persistence.LastAttemptedFinalText);
    }

    [Fact]
    public async Task Heartbeat_renews_lease_and_stops_after_success()
    {
        var delay = new TriggerOnceDelay();
        var fixture = new Fixture(delay: delay);
        fixture.Model.Events =
        [
            new ModelStreamEvent.TextDelta("Hello"),
            new ModelStreamEvent.Completed(ModelFinishReason.Stop),
        ];
        fixture.Model.WaitBeforeFirstEvent = ReleaseAfterObservedAsync(delay);

        await fixture.RunAsync();

        Assert.Equal(1, fixture.Renewal.Calls);
        Assert.True(delay.CancellationObserved);
    }

    [Fact]
    public async Task Lost_lease_cancels_provider_and_fails_with_partial_text()
    {
        var delay = new TriggerOnceDelay();
        var fixture = new Fixture(delay: delay);
        fixture.Renewal.Exception = new InvalidOperationException("lease owner changed");
        fixture.Model.StreamFactory = token => PartialThenWaitForCancellation(delay, token);

        var exception = await Assert.ThrowsAsync<ModelRunException>(() => fixture.RunAsync());

        Assert.Equal(ModelRunErrorCode.LeaseLost, exception.Code);
        Assert.Equal("Hel", fixture.Persistence.FailedText);
        Assert.Equal(1, fixture.Renewal.Calls);
        Assert.True(delay.CancellationObserved);
    }

    [Fact]
    public async Task Provider_cancelled_completion_marks_assistant_cancelled()
    {
        var fixture = new Fixture();
        fixture.Model.Events =
        [
            new ModelStreamEvent.TextDelta("partial"),
            new ModelStreamEvent.Completed(ModelFinishReason.Cancelled),
        ];

        var exception = await Assert.ThrowsAsync<ModelRunException>(() => fixture.RunAsync());

        Assert.Equal(ModelRunErrorCode.ProviderCancelled, exception.Code);
        Assert.Equal("partial", fixture.Persistence.CancelledText);
        Assert.Equal(1, fixture.Persistence.CancelCalls);
    }


    private static async Task ReleaseAfterObservedAsync(TriggerOnceDelay delay)
    {
        await delay.FirstDelayObserved.Task;
        delay.ReleaseHeartbeat();
    }

    private static async IAsyncEnumerable<ModelStreamEvent> StreamAcrossPeriodicFlush(
        PeriodicFlushDelay delay,
        RecordingPersistence persistence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ModelStreamEvent.TextDelta("Hel");
        await delay.FlushDelayScheduled.Task.WaitAsync(cancellationToken);
        delay.ReleaseFlushDelay();
        await persistence.IntermediateFlushObserved.Task.WaitAsync(cancellationToken);
        yield return new ModelStreamEvent.TextDelta("lo");
        yield return new ModelStreamEvent.Completed(ModelFinishReason.Stop);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> PartialThenFail(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelStreamEvent.TextDelta("Hel");
        await Task.Yield();
        throw new ModelProviderException(
            ModelProviderErrorCode.ConnectionFailed,
            "The provider connection ended unexpectedly.");
    }

    private static async IAsyncEnumerable<ModelStreamEvent> PartialThenCancel(
        CancellationTokenSource cancellation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ModelStreamEvent.TextDelta("Hel");
        cancellation.Cancel();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> CancelBeforeText(
        CancellationTokenSource cancellation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellation.Cancel();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> PartialThenWaitForCancellation(
        TriggerOnceDelay delay,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ModelStreamEvent.TextDelta("Hel");
        delay.ReleaseHeartbeat();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class Fixture
    {
        public Fixture(
            StreamingRunOptions? options = null,
            IAsyncDelay? delay = null)
        {
            Context = new ProjectContext(
                "/workspace/project",
                "/workspace/project",
                false,
                null,
                ProjectPlatform.Linux,
                UtcNow.Date,
                ProjectId.New());
            Prepared = CreatePrepared(Context.ProjectId);
            Clock = new MutableClock(UtcNow);
            Prepare = new StubPrepareSessionRun(Prepared);
            Model = new StubChatModelClient(Prepare);
            Persistence = new RecordingPersistence();
            Output = new RecordingOutputSink();
            Renewal = new RecordingRenewalService();
            Delay = delay ?? new BlockingDelay();
            Options = options ?? new StreamingRunOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
                FlushCharacterThreshold = 256,
                LeaseRenewInterval = TimeSpan.FromMinutes(1),
            };
        }

        public ProjectContext Context { get; }
        public PrepareSessionRunResult Prepared { get; }
        public MutableClock Clock { get; }
        public StubPrepareSessionRun Prepare { get; }
        public StubChatModelClient Model { get; }
        public RecordingPersistence Persistence { get; }
        public RecordingOutputSink Output { get; }
        public RecordingRenewalService Renewal { get; }
        public IAsyncDelay Delay { get; }
        public StreamingRunOptions Options { get; }

        public Task<RunPromptResult> RunAsync(CancellationToken cancellationToken = default)
        {
            var service = new RunPrompt(
                new StubProjectContextFactory(Context),
                Prepare,
                new ChatModelRequestBuilder(new ChatModelHistoryPolicy()),
                Model,
                Persistence,
                Renewal,
                Output,
                Clock,
                Delay,
                Options);

            return service.ExecuteAsync(
                new RunPromptRequest("Explain this project", Context.ProjectRoot),
                cancellationToken);
        }

        private static PrepareSessionRunResult CreatePrepared(ProjectId projectId)
        {
            var project = new Project(
                projectId,
                "/workspace/project",
                false,
                null,
                UtcNow);
            var session = new Session(SessionId.New(), projectId, UtcNow);
            session.Start(UtcNow);

            var user = new Message(
                MessageId.New(),
                session.Id,
                MessageRole.User,
                1,
                UtcNow);
            user.AddTextPart(MessagePartId.New(), 1, "Explain this project", UtcNow);
            user.Complete(UtcNow);

            var assistant = new Message(
                MessageId.New(),
                session.Id,
                MessageRole.Assistant,
                2,
                UtcNow);
            assistant.AddTextPart(MessagePartId.New(), 1, string.Empty, UtcNow);
            assistant.StartStreaming(UtcNow);

            var lease = new RunLease(
                session.Id,
                RunLeaseId.New(),
                UtcNow,
                UtcNow.AddMinutes(5));

            return new PrepareSessionRunResult(
                project,
                session,
                user,
                assistant,
                [],
                lease);
        }
    }

    private sealed class StubProjectContextFactory(ProjectContext context)
        : IProjectContextFactory
    {
        public Task<ProjectContext> CreateAsync(
            string? startPath = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context);
        }
    }

    private sealed class StubPrepareSessionRun(PrepareSessionRunResult result)
        : IPrepareSessionRun
    {
        public bool Called { get; private set; }
        public bool WasCalledBeforeModel { get; set; }

        public Task<PrepareSessionRunResult> ExecuteAsync(
            PrepareSessionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Called = true;
            return Task.FromResult(result);
        }
    }

    private sealed class StubChatModelClient(StubPrepareSessionRun prepare)
        : IChatModelClient
    {
        public IReadOnlyList<ModelStreamEvent> Events { get; set; } = [];
        public Func<CancellationToken, IAsyncEnumerable<ModelStreamEvent>>? StreamFactory { get; set; }
        public Action<int>? OnBeforeEvent { get; set; }
        public Task? WaitBeforeFirstEvent { get; set; }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken)
        {
            prepare.WasCalledBeforeModel = prepare.Called;
            Assert.Equal(1, request.Messages.Count(message =>
                message.Role == ChatModelRole.User &&
                message.Content == "Explain this project"));
            return StreamFactory is null
                ? StreamConfiguredEvents(cancellationToken)
                : StreamFactory(cancellationToken);
        }

        private async IAsyncEnumerable<ModelStreamEvent> StreamConfiguredEvents(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (WaitBeforeFirstEvent is not null)
            {
                await WaitBeforeFirstEvent.WaitAsync(cancellationToken);
            }

            for (var index = 0; index < Events.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OnBeforeEvent?.Invoke(index);
                yield return Events[index];
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingPersistence : IStreamingRunPersistence
    {
        public List<string> IntermediateTexts { get; } = [];
        public TaskCompletionSource IntermediateFlushObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CompletedText { get; private set; }
        public string? FailedText { get; private set; }
        public string? CancelledText { get; private set; }
        public string? LastAttemptedFinalText { get; private set; }
        public int CompleteCalls { get; private set; }
        public int FailCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public bool ThrowOnIntermediateFlush { get; set; }
        public bool ThrowOnComplete { get; set; }
        public bool ThrowOnFail { get; set; }

        public Task FlushAssistantTextAsync(
            MessageId assistantMessageId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnIntermediateFlush)
            {
                throw new InvalidOperationException("intermediate flush failed");
            }

            IntermediateTexts.Add(completeText);
            IntermediateFlushObserved.TrySetResult();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            LastAttemptedFinalText = completeText;
            if (ThrowOnComplete)
            {
                throw new InvalidOperationException("final flush failed");
            }

            CompletedText = completeText;
            return Task.CompletedTask;
        }

        public Task FailAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            FailCalls++;
            LastAttemptedFinalText = completeText;
            if (ThrowOnFail)
            {
                throw new InvalidOperationException("failure finalization failed");
            }

            FailedText = completeText;
            return Task.CompletedTask;
        }

        public Task CancelAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            LastAttemptedFinalText = completeText;
            CancelledText = completeText;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutputSink : IModelOutputSink
    {
        public List<string> Deltas { get; } = [];
        public bool Completed { get; private set; }

        public Task WriteDeltaAsync(
            string delta,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deltas.Add(delta);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRenewalService : IRunLeaseRenewalService
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; set; }

        public Task RenewAsync(
            SessionId sessionId,
            RunLeaseId leaseId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Exception is null
                ? Task.CompletedTask
                : Task.FromException(Exception);
        }
    }

    private sealed class MutableClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan duration)
        {
            UtcNow = UtcNow.Add(duration);
        }
    }

    private sealed class BlockingDelay : IAsyncDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class PeriodicFlushDelay : IAsyncDelay
    {
        private readonly TaskCompletionSource _releaseFlush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FlushDelayScheduled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            if (delay <= TimeSpan.FromSeconds(1))
            {
                FlushDelayScheduled.TrySetResult();
                return _releaseFlush.Task.WaitAsync(cancellationToken);
            }

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void ReleaseFlushDelay()
        {
            _releaseFlush.TrySetResult();
        }
    }

    private sealed class TriggerOnceDelay : IAsyncDelay
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource FirstDelayObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstDelayObserved.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public void ReleaseHeartbeat()
        {
            _release.TrySetResult();
        }
    }
}
