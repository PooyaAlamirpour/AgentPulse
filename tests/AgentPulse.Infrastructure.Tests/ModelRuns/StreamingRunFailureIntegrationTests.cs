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
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using AgentPulse.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.ModelRuns;

public sealed class StreamingRunFailureIntegrationTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Cancellation_after_Hel_preserves_text_and_cleans_session_and_lease()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await Fixture.CreateAsync(
            token => CancelAfterHel(cancellation, token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.RunAsync(cancellation.Token));

        await fixture.AssertFinalStateAsync("Hel", MessageStatus.Cancelled);
    }

    [Fact]
    public async Task Failure_after_Hel_preserves_text_and_cleans_session_and_lease()
    {
        await using var fixture = await Fixture.CreateAsync(FailAfterHel);

        var exception = await Assert.ThrowsAsync<ModelRunException>(() =>
            fixture.RunAsync(CancellationToken.None));

        Assert.Equal(ModelRunErrorCode.ProviderFailure, exception.Code);
        var providerException = Assert.IsType<ModelProviderException>(exception.InnerException);
        Assert.Equal(ModelFailureStage.AfterFirstToken, providerException.FailureStage);
        await fixture.AssertFinalStateAsync("Hel", MessageStatus.Failed);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> CancelAfterHel(
        CancellationTokenSource cancellation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ModelStreamEvent.TextDelta("Hel");
        cancellation.Cancel();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<ModelStreamEvent> FailAfterHel(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelStreamEvent.TextDelta("Hel");
        await Task.Yield();
        throw new ModelProviderException(
            ModelProviderErrorCode.ConnectionFailed,
            "The local test connection ended.",
            ModelFailureStage.AfterFirstToken);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteTestDatabase _database;
        private readonly AgentPulseDbContext _preparationContext;
        private readonly RunPrompt _service;
        private SessionId? _preparedSessionId;
        private MessageId? _preparedAssistantId;

        private Fixture(
            SqliteTestDatabase database,
            AgentPulseDbContext preparationContext,
            RunPrompt service)
        {
            _database = database;
            _preparationContext = preparationContext;
            _service = service;
        }

        public static async Task<Fixture> CreateAsync(
            Func<CancellationToken, IAsyncEnumerable<ModelStreamEvent>> streamFactory)
        {
            var database = await SqliteTestDatabase.CreateAsync();
            var factory = new TestDbContextFactory(database.Options);
            var preparationContext = database.CreateContext();
            var clock = new FixedClock(UtcNow);
            var prepareInner = new PrepareSessionRun(
                new ProjectRepository(preparationContext),
                new SessionRepository(preparationContext),
                new MessageRepository(preparationContext),
                new RunLeaseRepository(preparationContext),
                new UnitOfWork(preparationContext),
                clock,
                new SessionRunOptions { LeaseDuration = TimeSpan.FromMinutes(5) });
            var trackingPrepare = new TrackingPrepare(prepareInner);
            var context = new ProjectContext(
                "/workspace/project",
                "/workspace/project",
                false,
                null,
                ProjectPlatform.Linux,
                UtcNow.Date,
                ProjectId.New());
            var service = new RunPrompt(
                new StubProjectContextFactory(context),
                trackingPrepare,
                new ChatModelRequestBuilder(new ChatModelHistoryPolicy()),
                new TestModelClient(streamFactory),
                new StreamingRunPersistence(factory, clock),
                new RunLeaseRenewalService(
                    factory,
                    clock,
                    new SessionRunOptions { LeaseDuration = TimeSpan.FromMinutes(5) }),
                new RecordingOutputSink(),
                clock,
                new BlockingDelay(),
                new StreamingRunOptions
                {
                    FlushInterval = TimeSpan.FromHours(1),
                    FlushCharacterThreshold = 256,
                    LeaseRenewInterval = TimeSpan.FromMinutes(1),
                });
            var fixture = new Fixture(database, preparationContext, service);
            trackingPrepare.OnPrepared = value =>
            {
                fixture._preparedSessionId = value.Session.Id;
                fixture._preparedAssistantId = value.AssistantMessage.Id;
            };
            return fixture;
        }

        public async Task<RunPromptResult> RunAsync(CancellationToken cancellationToken)
        {
            return await _service.ExecuteAsync(
                new RunPromptRequest("test prompt", "/workspace/project"),
                cancellationToken);
        }

        public async Task AssertFinalStateAsync(
            string expectedText,
            MessageStatus expectedMessageStatus)
        {
            Assert.NotNull(_preparedSessionId);
            Assert.NotNull(_preparedAssistantId);

            await using var context = _database.CreateContext();
            var message = await context.Messages
                .Include(value => value.Parts)
                .SingleAsync(value => value.Id == _preparedAssistantId.Value);
            var session = await context.Sessions
                .SingleAsync(value => value.Id == _preparedSessionId.Value);
            var lease = await context.RunLeases
                .SingleOrDefaultAsync(value => value.SessionId == _preparedSessionId.Value);

            Assert.Equal(expectedText, Assert.IsType<TextMessagePart>(Assert.Single(message.Parts)).Text);
            Assert.Equal(expectedMessageStatus, message.Status);
            Assert.Equal(SessionStatus.Idle, session.Status);
            Assert.Null(lease);
        }

        public async ValueTask DisposeAsync()
        {
            await _preparationContext.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    private sealed class TrackingPrepare(IPrepareSessionRun inner) : IPrepareSessionRun
    {
        public Action<PrepareSessionRunResult>? OnPrepared { get; set; }

        public async Task<PrepareSessionRunResult> ExecuteAsync(
            PrepareSessionRunRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.ExecuteAsync(request, cancellationToken);
            OnPrepared?.Invoke(result);
            return result;
        }
    }

    private sealed class TestModelClient(
        Func<CancellationToken, IAsyncEnumerable<ModelStreamEvent>> streamFactory)
        : IChatModelClient
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken) => streamFactory(cancellationToken);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AgentPulseDbContext> options)
        : IDbContextFactory<AgentPulseDbContext>
    {
        public AgentPulseDbContext CreateDbContext() => new(options);

        public Task<AgentPulseDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
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

    private sealed class FixedClock(DateTime value) : IClock
    {
        public DateTime UtcNow { get; } = value;
    }

    private sealed class BlockingDelay : IAsyncDelay
    {
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class RecordingOutputSink : IModelOutputSink
    {
        public Task WriteDeltaAsync(
            string delta,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CompleteAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
