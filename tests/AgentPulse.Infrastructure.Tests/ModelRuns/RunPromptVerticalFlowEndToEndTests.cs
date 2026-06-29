using System.Net;
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
using AgentPulse.Infrastructure.ProjectContexts;
using AgentPulse.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.ModelRuns;

public sealed class RunPromptVerticalFlowEndToEndTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task New_session_streams_before_completion_checkpoints_and_releases_lock()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        var fake = FakeChatModelClient.Controlled(
            "Hel",
            "lo",
            new ModelUsage(4, 2, 6));
        var output = new RecordingOutputSink();
        var persistence = new CoordinatedPersistence(
            new StreamingRunPersistence(new TestDbContextFactory(database.Options), new FixedClock()));
        await using var run = CreateRunService(
            database,
            projectContext,
            fake,
            output,
            persistence,
            new StreamingRunOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
                FlushCharacterThreshold = 3,
                LeaseRenewInterval = TimeSpan.FromMinutes(1),
            });

        var runTask = run.Service.ExecuteAsync(
            new RunPromptRequest("first question", projectContext.CurrentDirectory));

        await output.FirstDeltaWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await persistence.FirstCheckpointWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runTask.IsCompleted);
        Assert.Equal(["Hel"], output.Deltas);

        await using (var streamingState = database.CreateContext())
        {
            var messages = await streamingState.Messages
                .Include(message => message.Parts)
                .OrderBy(message => message.Sequence)
                .ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Equal(MessageStatus.Completed, messages[0].Status);
            Assert.Equal(MessageStatus.Streaming, messages[1].Status);
            Assert.Equal(
                "Hel",
                Assert.IsType<TextMessagePart>(Assert.Single(messages[1].Parts)).Text);
            Assert.Equal(1, await streamingState.RunLeases.CountAsync());
        }

        fake.ReleaseControlledStream();
        var result = await runTask;

        Assert.Equal("Hello", result.Text);
        Assert.Equal(["Hel", "lo"], output.Deltas);
        Assert.True(output.Completed);
        Assert.Equal("default-model", Assert.Single(fake.Requests).Model);

        await using var finalState = database.CreateContext();
        var assistant = await finalState.Messages
            .Include(message => message.Parts)
            .SingleAsync(message => message.Id == result.AssistantMessageId);
        var session = await finalState.Sessions.SingleAsync(
            session => session.Id == result.SessionId);
        Assert.Equal(MessageStatus.Completed, assistant.Status);
        Assert.Equal("Hello", AssertText(assistant));
        Assert.Equal("default-model", assistant.Model);
        Assert.Equal(ModelFinishReason.Stop.ToString(), assistant.FinishReason);
        Assert.Equal(4, assistant.InputTokens);
        Assert.Equal(2, assistant.OutputTokens);
        Assert.Equal(6, assistant.TotalTokens);
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Empty(await finalState.RunLeases.ToListAsync());
    }

    [Fact]
    public async Task Existing_session_request_contains_only_its_completed_history()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();

        var firstFake = FakeChatModelClient.Immediate("first answer");
        RunPromptResult firstResult;
        await using (var firstRun = CreateRunService(database, projectContext, firstFake))
        {
            firstResult = await firstRun.Service.ExecuteAsync(
                new RunPromptRequest("first question", projectContext.CurrentDirectory));
        }

        var otherFake = FakeChatModelClient.Immediate("other answer");
        await using (var otherRun = CreateRunService(database, projectContext, otherFake))
        {
            await otherRun.Service.ExecuteAsync(
                new RunPromptRequest("other question", projectContext.CurrentDirectory));
        }

        var continuationFake = FakeChatModelClient.Immediate("second answer");
        await using (var continuation = CreateRunService(
                         database,
                         projectContext,
                         continuationFake))
        {
            await continuation.Service.ExecuteAsync(
                new RunPromptRequest(
                    "second question",
                    projectContext.CurrentDirectory,
                    firstResult.SessionId));
        }

        var request = Assert.Single(continuationFake.Requests);
        Assert.Equal(
            [
                ChatModelRole.System,
                ChatModelRole.User,
                ChatModelRole.Assistant,
                ChatModelRole.User,
            ],
            request.Messages.Select(message => message.Role));
        Assert.Equal(
            ["first question", "first answer", "second question"],
            request.Messages.Skip(1).Select(message => message.Content));
        Assert.DoesNotContain(
            request.Messages,
            message => message.Content?.Contains("other", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Concurrent_run_is_rejected_without_messages_and_next_run_can_continue()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        var firstFake = FakeChatModelClient.Controlled("partial", " complete");
        var firstOutput = new RecordingOutputSink();
        await using var firstRun = CreateRunService(
            database,
            projectContext,
            firstFake,
            firstOutput);

        var firstTask = firstRun.Service.ExecuteAsync(
            new RunPromptRequest("first", projectContext.CurrentDirectory));
        await firstOutput.FirstDeltaWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));

        SessionId sessionId;
        await using (var activeState = database.CreateContext())
        {
            sessionId = await activeState.Sessions.Select(session => session.Id).SingleAsync();
            Assert.Equal(2, await activeState.Messages.CountAsync());
        }

        var rejectedFake = FakeChatModelClient.Immediate("must not run");
        await using (var rejectedRun = CreateRunService(database, projectContext, rejectedFake))
        {
            var exception = await Assert.ThrowsAsync<SessionRunException>(() =>
                rejectedRun.Service.ExecuteAsync(
                    new RunPromptRequest(
                        "second",
                        projectContext.CurrentDirectory,
                        sessionId)));
            Assert.Equal(SessionRunErrorCode.SessionAlreadyRunning, exception.Code);
            Assert.Empty(rejectedFake.Requests);
        }

        await using (var stillActive = database.CreateContext())
        {
            Assert.Equal(2, await stillActive.Messages.CountAsync());
        }

        firstFake.ReleaseControlledStream();
        await firstTask;

        var thirdFake = FakeChatModelClient.Immediate("third answer");
        await using (var thirdRun = CreateRunService(database, projectContext, thirdFake))
        {
            await thirdRun.Service.ExecuteAsync(
                new RunPromptRequest(
                    "third",
                    projectContext.CurrentDirectory,
                    sessionId));
        }

        await using var finalState = database.CreateContext();
        Assert.Equal(4, await finalState.Messages.CountAsync());
        Assert.Empty(await finalState.RunLeases.ToListAsync());
        Assert.Equal(SessionStatus.Idle, (await finalState.Sessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Cancellation_preserves_partial_response_and_allows_next_run()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        using var cancellation = new CancellationTokenSource();
        var fake = FakeChatModelClient.WaitForCancellationAfter("partial");
        var output = new RecordingOutputSink();
        await using var cancelledRun = CreateRunService(database, projectContext, fake, output);

        var runTask = cancelledRun.Service.ExecuteAsync(
            new RunPromptRequest("cancel me", projectContext.CurrentDirectory),
            cancellation.Token);
        await output.FirstDeltaWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(fake.CancellationObserved);

        SessionId sessionId;
        await using (var cancelledState = database.CreateContext())
        {
            var messages = await cancelledState.Messages
                .Include(message => message.Parts)
                .OrderBy(message => message.Sequence)
                .ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Equal(MessageStatus.Completed, messages[0].Status);
            Assert.Equal(MessageStatus.Cancelled, messages[1].Status);
            Assert.Equal("partial", AssertText(messages[1]));
            sessionId = messages[0].SessionId;
            Assert.Empty(await cancelledState.RunLeases.ToListAsync());
            Assert.Equal(SessionStatus.Idle, (await cancelledState.Sessions.SingleAsync()).Status);
        }

        var nextFake = FakeChatModelClient.Immediate("recovered");
        await using (var nextRun = CreateRunService(database, projectContext, nextFake))
        {
            await nextRun.Service.ExecuteAsync(
                new RunPromptRequest(
                    "after cancellation",
                    projectContext.CurrentDirectory,
                    sessionId));
        }

        var nextRequest = Assert.Single(nextFake.Requests);
        Assert.DoesNotContain(
            nextRequest.Messages,
            message => message.Role == ChatModelRole.Assistant &&
                string.Equals(message.Content, "partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provider_failure_persists_safe_metadata_and_releases_lock()
    {
        const string unsafeProviderText = "raw-secret-provider-body";
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        var fake = FakeChatModelClient.FailAfter(
            "partial",
            new ModelProviderException(
                ModelProviderErrorCode.Unavailable,
                unsafeProviderText,
                ModelFailureStage.AfterFirstToken,
                httpStatusCode: HttpStatusCode.ServiceUnavailable));
        await using var run = CreateRunService(database, projectContext, fake);

        var exception = await Assert.ThrowsAsync<ModelRunException>(() =>
            run.Service.ExecuteAsync(
                new RunPromptRequest("private prompt", projectContext.CurrentDirectory)));

        Assert.Equal(ModelRunErrorCode.ProviderFailure, exception.Code);
        Assert.DoesNotContain(unsafeProviderText, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);

        await using var finalState = database.CreateContext();
        var assistant = await finalState.Messages
            .Include(message => message.Parts)
            .SingleAsync(message => message.Role == MessageRole.Assistant);
        Assert.Equal(MessageStatus.Failed, assistant.Status);
        Assert.Equal("partial", AssertText(assistant));
        Assert.Equal(ModelProviderErrorCode.Unavailable.ToString(), assistant.FailureKind);
        Assert.Equal(ModelFailureStage.AfterFirstToken.ToString(), assistant.FailureStage);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, assistant.FailureStatusCode);
        Assert.DoesNotContain(
            unsafeProviderText,
            assistant.FailureReason ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Empty(await finalState.RunLeases.ToListAsync());
        Assert.Equal(SessionStatus.Idle, (await finalState.Sessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Model_override_is_request_scoped_and_default_is_restored_next_run()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        var defaults = new ChatModelRunDefaults("default-model");

        var overrideFake = FakeChatModelClient.Immediate("custom answer");
        RunPromptResult overrideResult;
        await using (var overrideRun = CreateRunService(
                         database,
                         projectContext,
                         overrideFake,
                         defaults: defaults))
        {
            overrideResult = await overrideRun.Service.ExecuteAsync(
                new RunPromptRequest(
                    "custom",
                    projectContext.CurrentDirectory,
                    ModelOverride: "custom-model"));
        }

        var defaultFake = FakeChatModelClient.Immediate("default answer");
        RunPromptResult defaultResult;
        await using (var defaultRun = CreateRunService(
                         database,
                         projectContext,
                         defaultFake,
                         defaults: defaults))
        {
            defaultResult = await defaultRun.Service.ExecuteAsync(
                new RunPromptRequest("default", projectContext.CurrentDirectory));
        }

        Assert.Equal("custom-model", Assert.Single(overrideFake.Requests).Model);
        Assert.Equal("default-model", Assert.Single(defaultFake.Requests).Model);
        Assert.Equal("default-model", defaults.Model);

        await using var state = database.CreateContext();
        var customAssistant = await state.Messages.SingleAsync(
            message => message.Id == overrideResult.AssistantMessageId);
        var defaultAssistant = await state.Messages.SingleAsync(
            message => message.Id == defaultResult.AssistantMessageId);
        Assert.Equal("custom-model", customAssistant.Model);
        Assert.Equal("default-model", defaultAssistant.Model);
    }

    [Fact]
    public async Task Git_root_and_subdirectory_share_project_and_session_history()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AgentPulse Tests",
            Guid.NewGuid().ToString("N"));
        var repositoryPath = Directory.CreateDirectory(
            Path.Combine(root, "repository with space")).FullName;
        var subdirectory = Directory.CreateDirectory(
            Path.Combine(repositoryPath, "src", "service")).FullName;

        try
        {
            await using var database = await SqliteTestDatabase.CreateAsync();
            var projectContextFactory = new ProjectContextFactory(
                new SystemProjectFileSystem(),
                new ControlledGitService(repositoryPath),
                new FixedClock(),
                new SystemPlatformProvider(),
                new DeterministicProjectIdFactory());

            var firstFake = FakeChatModelClient.Immediate("first answer");
            RunPromptResult firstResult;
            await using (var firstRun = CreateRunService(
                             database,
                             CreateProjectContext(),
                             firstFake,
                             projectContextFactory: projectContextFactory))
            {
                firstResult = await firstRun.Service.ExecuteAsync(
                    new RunPromptRequest(
                        "first question",
                        repositoryPath + Path.DirectorySeparatorChar));
            }

            var continuationFake = FakeChatModelClient.Immediate("second answer");
            await using (var continuationRun = CreateRunService(
                             database,
                             CreateProjectContext(),
                             continuationFake,
                             projectContextFactory: projectContextFactory))
            {
                var secondResult = await continuationRun.Service.ExecuteAsync(
                    new RunPromptRequest(
                        "second question",
                        subdirectory,
                        firstResult.SessionId));
                Assert.Equal(firstResult.SessionId, secondResult.SessionId);
            }

            var secondRequest = Assert.Single(continuationFake.Requests);
            Assert.Equal(
                ["first question", "first answer", "second question"],
                secondRequest.Messages.Skip(1).Select(message => message.Content));

            await using var state = database.CreateContext();
            var project = await state.Projects.SingleAsync();
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)),
                project.NormalizedRootPath);
            Assert.True(project.IsGitRepository);
            Assert.Equal(project.NormalizedRootPath, project.GitWorktree);
            Assert.Equal(1, await state.Sessions.CountAsync());
            Assert.Equal(4, await state.Messages.CountAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Existing_session_from_another_repository_is_rejected_without_messages()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AgentPulse Tests",
            Guid.NewGuid().ToString("N"));
        var firstRepository = Directory.CreateDirectory(Path.Combine(root, "first repository")).FullName;
        var secondRepository = Directory.CreateDirectory(Path.Combine(root, "second repository")).FullName;

        try
        {
            await using var database = await SqliteTestDatabase.CreateAsync();
            var firstFactory = CreateConcreteProjectContextFactory(firstRepository);
            var firstFake = FakeChatModelClient.Immediate("first answer");
            RunPromptResult firstResult;
            await using (var firstRun = CreateRunService(
                             database,
                             CreateProjectContext(),
                             firstFake,
                             projectContextFactory: firstFactory))
            {
                firstResult = await firstRun.Service.ExecuteAsync(
                    new RunPromptRequest("first question", firstRepository));
            }

            var rejectedFake = FakeChatModelClient.Immediate("must not run");
            await using (var rejectedRun = CreateRunService(
                             database,
                             CreateProjectContext(),
                             rejectedFake,
                             projectContextFactory: CreateConcreteProjectContextFactory(secondRepository)))
            {
                var exception = await Assert.ThrowsAsync<SessionRunException>(() =>
                    rejectedRun.Service.ExecuteAsync(
                        new RunPromptRequest(
                            "private prompt",
                            secondRepository,
                            firstResult.SessionId)));
                Assert.Equal(SessionRunErrorCode.SessionProjectMismatch, exception.Code);
            }

            Assert.Empty(rejectedFake.Requests);
            await using var state = database.CreateContext();
            Assert.Equal(1, await state.Projects.CountAsync());
            Assert.Equal(2, await state.Messages.CountAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task First_clock_read_after_prepare_failure_finalizes_state_and_releases_lock()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        var failingClock = new ThrowOnSecondReadClock();
        var fake = FakeChatModelClient.Immediate("must not stream");
        await using var failedRun = CreateRunService(
            database,
            projectContext,
            fake,
            clock: failingClock);

        var exception = await Assert.ThrowsAsync<ModelRunException>(() =>
            failedRun.Service.ExecuteAsync(
                new RunPromptRequest("private prompt", projectContext.CurrentDirectory)));

        Assert.Equal(ModelRunErrorCode.UnexpectedFailure, exception.Code);
        Assert.DoesNotContain("private prompt", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);

        SessionId sessionId;
        await using (var failedState = database.CreateContext())
        {
            var messages = await failedState.Messages
                .Include(message => message.Parts)
                .OrderBy(message => message.Sequence)
                .ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Equal(MessageStatus.Completed, messages[0].Status);
            Assert.Equal(MessageStatus.Failed, messages[1].Status);
            Assert.Equal(string.Empty, AssertText(messages[1]));
            Assert.Equal("Unexpected", messages[1].FailureKind);
            Assert.DoesNotContain(
                "private prompt",
                messages[1].FailureReason ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Empty(await failedState.RunLeases.ToListAsync());
            var session = await failedState.Sessions.SingleAsync();
            Assert.Equal(SessionStatus.Idle, session.Status);
            sessionId = session.Id;
        }

        var recoveryFake = FakeChatModelClient.Immediate("recovered");
        await using (var recoveryRun = CreateRunService(database, projectContext, recoveryFake))
        {
            await recoveryRun.Service.ExecuteAsync(
                new RunPromptRequest(
                    "next prompt",
                    projectContext.CurrentDirectory,
                    sessionId));
        }

        await using var recoveredState = database.CreateContext();
        Assert.Equal(4, await recoveredState.Messages.CountAsync());
        Assert.Empty(await recoveredState.RunLeases.ToListAsync());
        Assert.Equal(SessionStatus.Idle, (await recoveredState.Sessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task First_console_write_failure_marks_assistant_failed_and_releases_lock()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        var fake = FakeChatModelClient.Immediate("partial");
        await using var run = CreateRunService(
            database,
            projectContext,
            fake,
            output: new ThrowingOutputSink());

        var exception = await Assert.ThrowsAsync<ModelRunException>(() =>
            run.Service.ExecuteAsync(
                new RunPromptRequest("private prompt", projectContext.CurrentDirectory)));

        Assert.Equal(ModelRunErrorCode.OutputFailure, exception.Code);
        Assert.DoesNotContain("private prompt", exception.Message, StringComparison.Ordinal);

        await using var finalState = database.CreateContext();
        var assistant = await finalState.Messages
            .Include(message => message.Parts)
            .SingleAsync(message => message.Role == MessageRole.Assistant);
        Assert.Equal(MessageStatus.Failed, assistant.Status);
        Assert.Equal("partial", AssertText(assistant));
        Assert.Equal(ModelRunErrorCode.OutputFailure.ToString(), assistant.FailureKind);
        Assert.Empty(await finalState.RunLeases.ToListAsync());
        Assert.Equal(SessionStatus.Idle, (await finalState.Sessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Second_checkpoint_failure_marks_run_failed_with_latest_partial_and_releases_lock()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var projectContext = CreateProjectContext();
        var fake = FakeChatModelClient.ImmediateFromDeltas("A", "B");
        var actualPersistence = new StreamingRunPersistence(
            new TestDbContextFactory(database.Options),
            new FixedClock());
        var persistence = new FailOnCheckpointPersistence(actualPersistence, checkpointNumber: 2);
        await using var run = CreateRunService(
            database,
            projectContext,
            fake,
            persistence: persistence,
            streamingOptions: new StreamingRunOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
                FlushCharacterThreshold = 1,
                LeaseRenewInterval = TimeSpan.FromMinutes(1),
            });

        var exception = await Assert.ThrowsAsync<ModelRunException>(() =>
            run.Service.ExecuteAsync(
                new RunPromptRequest("checkpoint", projectContext.CurrentDirectory)));

        Assert.Equal(ModelRunErrorCode.PersistenceFailure, exception.Code);
        Assert.Equal(2, persistence.CheckpointCalls);

        await using var finalState = database.CreateContext();
        var assistant = await finalState.Messages
            .Include(message => message.Parts)
            .SingleAsync(message => message.Role == MessageRole.Assistant);
        Assert.Equal(MessageStatus.Failed, assistant.Status);
        Assert.Equal("AB", AssertText(assistant));
        Assert.Equal(ModelRunErrorCode.PersistenceFailure.ToString(), assistant.FailureKind);
        Assert.Empty(await finalState.RunLeases.ToListAsync());
        Assert.Equal(SessionStatus.Idle, (await finalState.Sessions.SingleAsync()).Status);
    }

    private static RunService CreateRunService(
        SqliteTestDatabase database,
        ProjectContext projectContext,
        IChatModelClient modelClient,
        IModelOutputSink? output = null,
        IStreamingRunPersistence? persistence = null,
        StreamingRunOptions? streamingOptions = null,
        ChatModelRunDefaults? defaults = null,
        IClock? clock = null,
        IProjectContextFactory? projectContextFactory = null)
    {
        var preparationContext = database.CreateContext();
        clock ??= new FixedClock();
        var dbContextFactory = new TestDbContextFactory(database.Options);
        var sessionOptions = new SessionRunOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(5),
        };
        var prepare = new PrepareSessionRun(
            new ProjectRepository(preparationContext),
            new SessionRepository(preparationContext),
            new MessageRepository(preparationContext),
            new RunLeaseRepository(preparationContext),
            new UnitOfWork(preparationContext),
            clock,
            sessionOptions);
        var end = new EndSessionRun(
            new SessionRepository(preparationContext),
            new RunLeaseRepository(preparationContext),
            new UnitOfWork(preparationContext),
            clock);
        var service = new RunPrompt(
            projectContextFactory ?? new StubProjectContextFactory(projectContext),
            prepare,
            new ChatModelRequestBuilder(new ChatModelHistoryPolicy()),
            modelClient,
            persistence ?? new StreamingRunPersistence(dbContextFactory, clock),
            new RunLeaseRenewalService(dbContextFactory, clock, sessionOptions),
            end,
            output ?? new RecordingOutputSink(),
            clock,
            new BlockingDelay(),
            defaults ?? new ChatModelRunDefaults("default-model"),
            streamingOptions ?? new StreamingRunOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
                FlushCharacterThreshold = 256,
                LeaseRenewInterval = TimeSpan.FromMinutes(1),
            });
        return new RunService(service, preparationContext);
    }

    private static ProjectContextFactory CreateConcreteProjectContextFactory(string repositoryPath)
    {
        return new ProjectContextFactory(
            new SystemProjectFileSystem(),
            new ControlledGitService(repositoryPath),
            new FixedClock(),
            new SystemPlatformProvider(),
            new DeterministicProjectIdFactory());
    }

    private static ProjectContext CreateProjectContext()
    {
        return new ProjectContext(
            "/workspace/project",
            "/workspace/project",
            false,
            null,
            ProjectPlatform.Linux,
            UtcNow.Date,
            ProjectId.New());
    }

    private static string AssertText(Message message)
    {
        return Assert.IsType<TextMessagePart>(Assert.Single(message.Parts)).Text;
    }

    private sealed class RunService(RunPrompt service, AgentPulseDbContext context)
        : IAsyncDisposable
    {
        public RunPrompt Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class StubProjectContextFactory(ProjectContext context)
        : IProjectContextFactory
    {
        public Task<ProjectContext> CreateAsync(
            string? inputPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context);
        }
    }

    private sealed class ControlledGitService(string repositoryPath) : IGitService
    {
        private readonly string _repositoryPath =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }

        public Task<GitRepositoryInfo?> DiscoverAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<GitRepositoryInfo?>(new GitRepositoryInfo(
                _repositoryPath,
                Path.Combine(_repositoryPath, ".git"),
                Path.Combine(_repositoryPath, ".git"),
                false));
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => RunPromptVerticalFlowEndToEndTests.UtcNow;
    }

    private sealed class ThrowOnSecondReadClock : IClock
    {
        private int _readCount;

        public DateTime UtcNow
        {
            get
            {
                _readCount++;
                if (_readCount == 2)
                {
                    throw new InvalidOperationException("Injected post-prepare clock failure.");
                }

                return RunPromptVerticalFlowEndToEndTests.UtcNow;
            }
        }
    }

    private sealed class ThrowingOutputSink : IModelOutputSink
    {
        public Task WriteDeltaAsync(
            string delta,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Injected output failure.");
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
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

    private sealed class RecordingOutputSink : IModelOutputSink
    {
        public List<string> Deltas { get; } = [];

        public TaskCompletionSource FirstDeltaWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Completed { get; private set; }

        public Task WriteDeltaAsync(
            string delta,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deltas.Add(delta);
            FirstDeltaWritten.TrySetResult();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChatModelClient(
        Func<CancellationToken, IAsyncEnumerable<ModelStreamEvent>> streamFactory)
        : IChatModelClient
    {
        private readonly TaskCompletionSource _continueControlledStream =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ChatModelRequest> Requests { get; } = [];

        public bool CancellationObserved { get; private set; }

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ObserveCancellationAsync(streamFactory(cancellationToken), cancellationToken);
        }

        public void ReleaseControlledStream()
        {
            _continueControlledStream.TrySetResult();
        }

        public static FakeChatModelClient Immediate(string text)
        {
            return ImmediateFromDeltas(text);
        }

        public static FakeChatModelClient ImmediateFromDeltas(params string[] deltas)
        {
            return new FakeChatModelClient(token => Complete(deltas, null, token));
        }

        public static FakeChatModelClient Controlled(
            string firstDelta,
            string finalDelta,
            ModelUsage? usage = null)
        {
            FakeChatModelClient? client = null;
            client = new FakeChatModelClient(token => ControlledStream(
                client!,
                firstDelta,
                finalDelta,
                usage,
                token));
            return client;
        }

        public static FakeChatModelClient WaitForCancellationAfter(string delta)
        {
            return new FakeChatModelClient(token => WaitForCancellation(delta, token));
        }

        public static FakeChatModelClient FailAfter(string delta, Exception exception)
        {
            return new FakeChatModelClient(token => Fail(delta, exception, token));
        }

        private async IAsyncEnumerable<ModelStreamEvent> ObserveCancellationAsync(
            IAsyncEnumerable<ModelStreamEvent> source,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var enumerator = source.GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    throw;
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }

        private static async IAsyncEnumerable<ModelStreamEvent> Complete(
            IReadOnlyList<string> deltas,
            ModelUsage? usage,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var delta in deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelStreamEvent.TextDelta(delta);
                await Task.Yield();
            }

            if (usage is not null)
            {
                yield return new ModelStreamEvent.Usage(usage);
            }

            yield return new ModelStreamEvent.Completed(ModelFinishReason.Stop);
        }

        private static async IAsyncEnumerable<ModelStreamEvent> ControlledStream(
            FakeChatModelClient client,
            string firstDelta,
            string finalDelta,
            ModelUsage? usage,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ModelStreamEvent.TextDelta(firstDelta);
            await client._continueControlledStream.Task.WaitAsync(cancellationToken);
            yield return new ModelStreamEvent.TextDelta(finalDelta);
            if (usage is not null)
            {
                yield return new ModelStreamEvent.Usage(usage);
            }

            yield return new ModelStreamEvent.Completed(ModelFinishReason.Stop);
        }

        private static async IAsyncEnumerable<ModelStreamEvent> WaitForCancellation(
            string delta,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ModelStreamEvent.TextDelta(delta);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        private static async IAsyncEnumerable<ModelStreamEvent> Fail(
            string delta,
            Exception exception,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelStreamEvent.TextDelta(delta);
            await Task.Yield();
            throw exception;
        }
    }

    private sealed class CoordinatedPersistence(IStreamingRunPersistence inner)
        : IStreamingRunPersistence
    {
        public TaskCompletionSource FirstCheckpointWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task FlushAssistantTextAsync(
            MessageId assistantMessageId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            await inner.FlushAssistantTextAsync(
                assistantMessageId,
                completeText,
                cancellationToken);
            FirstCheckpointWritten.TrySetResult();
        }

        public Task CompleteAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            AssistantCompletionMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            return inner.CompleteAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                metadata,
                cancellationToken);
        }

        public Task FailAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            AssistantFailureMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            return inner.FailAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                metadata,
                cancellationToken);
        }

        public Task CancelAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            string model,
            CancellationToken cancellationToken = default)
        {
            return inner.CancelAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                model,
                cancellationToken);
        }
    }

    private sealed class FailOnCheckpointPersistence(
        IStreamingRunPersistence inner,
        int checkpointNumber) : IStreamingRunPersistence
    {
        public int CheckpointCalls { get; private set; }

        public async Task FlushAssistantTextAsync(
            MessageId assistantMessageId,
            string completeText,
            CancellationToken cancellationToken = default)
        {
            CheckpointCalls++;
            if (CheckpointCalls == checkpointNumber)
            {
                throw new InvalidOperationException("Injected checkpoint failure.");
            }

            await inner.FlushAssistantTextAsync(
                assistantMessageId,
                completeText,
                cancellationToken);
        }

        public Task CompleteAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            AssistantCompletionMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            return inner.CompleteAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                metadata,
                cancellationToken);
        }

        public Task FailAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            AssistantFailureMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            return inner.FailAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                metadata,
                cancellationToken);
        }

        public Task CancelAsync(
            SessionId sessionId,
            MessageId assistantMessageId,
            RunLeaseId leaseId,
            string completeText,
            string model,
            CancellationToken cancellationToken = default)
        {
            return inner.CancelAsync(
                sessionId,
                assistantMessageId,
                leaseId,
                completeText,
                model,
                cancellationToken);
        }
    }
}
