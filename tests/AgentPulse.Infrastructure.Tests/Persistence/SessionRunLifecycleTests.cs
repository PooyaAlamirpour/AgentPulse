using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class SessionRunLifecycleTests
{
    private static readonly DateTime InitialUtc =
        new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Project_registration_is_idempotent_and_updates_last_seen_data()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/original");

        await using (var context = database.CreateContext())
        {
            var useCase = CreateRegisterProject(context, clock);
            var created = await useCase.ExecuteAsync(projectContext);

            Assert.Equal(projectContext.ProjectId, created.Id);
            Assert.Equal(InitialUtc, created.CreatedAtUtc);
        }

        clock.UtcNow = InitialUtc.AddHours(1);
        var updatedContext = CreateProjectContext(projectContext.ProjectId, "/workspace/moved");

        await using (var context = database.CreateContext())
        {
            var useCase = CreateRegisterProject(context, clock);
            var loaded = await useCase.ExecuteAsync(updatedContext);

            Assert.Equal("/workspace/moved", loaded.NormalizedRootPath);
            Assert.Equal(InitialUtc, loaded.CreatedAtUtc);
            Assert.Equal(clock.UtcNow, loaded.UpdatedAtUtc);
        }

        await using var verificationContext = database.CreateContext();
        Assert.Equal(1, await verificationContext.Projects.CountAsync());
    }

    [Fact]
    public async Task Session_can_be_created_and_continued_only_inside_its_project()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/project-a");

        Session session;
        await using (var context = database.CreateContext())
        {
            var project = await CreateRegisterProject(context, clock).ExecuteAsync(projectContext);
            session = await CreateSessionUseCase(context, clock).ExecuteAsync(project.Id);

            Assert.Equal(SessionStatus.Idle, session.Status);
            Assert.Equal(DateTimeKind.Utc, session.CreatedAtUtc.Kind);
        }

        await using (var context = database.CreateContext())
        {
            var continued = await new ContinueSession(new SessionRepository(context))
                .ExecuteAsync(projectContext.ProjectId, session.Id);
            Assert.Equal(session.Id, continued.Id);
        }

        await using (var context = database.CreateContext())
        {
            var exception = await Assert.ThrowsAsync<SessionRunException>(() =>
                new ContinueSession(new SessionRepository(context))
                    .ExecuteAsync(ProjectId.New(), session.Id));
            Assert.Equal(SessionRunErrorCode.SessionProjectMismatch, exception.Code);
        }
    }

    [Fact]
    public async Task Prepare_commits_messages_parts_sequences_session_and_lease_before_returning()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/prepare");

        PrepareSessionRunResult result;
        await using (var context = database.CreateContext())
        {
            result = await CreatePrepare(context, clock).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, null, "hello provider"));
        }

        Assert.Empty(result.OrderedPreviousHistory);
        Assert.Equal(SessionStatus.Running, result.Session.Status);
        Assert.Equal(MessageRole.User, result.UserMessage.Role);
        Assert.Equal(MessageStatus.Completed, result.UserMessage.Status);
        Assert.Equal("hello provider", AssertText(result.UserMessage));
        Assert.Equal(MessageRole.Assistant, result.AssistantMessage.Role);
        Assert.Equal(MessageStatus.Streaming, result.AssistantMessage.Status);
        Assert.Equal(string.Empty, AssertText(result.AssistantMessage));
        Assert.Equal(result.UserMessage.Sequence + 1, result.AssistantMessage.Sequence);
        Assert.Equal(result.Session.Id, result.RunLease.SessionId);
        Assert.Equal(DateTimeKind.Utc, result.Project.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.Project.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.Session.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.Session.UpdatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.UserMessage.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.AssistantMessage.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.RunLease.AcquiredAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.RunLease.ExpiresAtUtc.Kind);

        await using var verificationContext = database.CreateContext();
        var persistedMessages = await new MessageRepository(verificationContext)
            .ListBySessionIdAsync(result.Session.Id);
        Assert.Equal(2, persistedMessages.Count);
        Assert.Equal(new long[] { 1, 2 }, persistedMessages.Select(message => message.Sequence));
        Assert.Equal(1, await verificationContext.RunLeases.CountAsync());
        Assert.Equal(SessionStatus.Running, (await verificationContext.Sessions.SingleAsync()).Status);
        Assert.All(
            persistedMessages.SelectMany(message => message.Parts),
            part => Assert.Equal(DateTimeKind.Utc, part.CreatedAtUtc.Kind));
    }

    [Fact]
    public async Task Previous_history_is_ordered_and_separate_from_the_new_run_messages()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/history");
        SessionId sessionId;
        RunLeaseId firstLeaseId;

        await using (var context = database.CreateContext())
        {
            var first = await CreatePrepare(context, clock).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, null, "first"));
            sessionId = first.Session.Id;
            firstLeaseId = first.RunLease.LeaseId;
        }

        clock.UtcNow = InitialUtc.AddSeconds(1);
        await using (var context = database.CreateContext())
        {
            await CreateEnd(context, clock).ExecuteAsync(sessionId, firstLeaseId);
        }

        clock.UtcNow = InitialUtc.AddSeconds(2);
        PrepareSessionRunResult second;
        await using (var context = database.CreateContext())
        {
            second = await CreatePrepare(context, clock).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, sessionId, "second"));
        }

        Assert.Equal(new long[] { 1, 2 }, second.OrderedPreviousHistory.Select(message => message.Sequence));
        Assert.DoesNotContain(second.UserMessage, second.OrderedPreviousHistory);
        Assert.DoesNotContain(second.AssistantMessage, second.OrderedPreviousHistory);
        Assert.Equal(3, second.UserMessage.Sequence);
        Assert.Equal(4, second.AssistantMessage.Sequence);
    }

    [Fact]
    public async Task Preparation_rolls_back_every_change_when_assistant_insert_fails()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/rollback");

        await using (var triggerContext = database.CreateContext())
        {
            await triggerContext.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectAssistantMessage
                BEFORE INSERT ON Messages
                WHEN NEW.Role = 'Assistant'
                BEGIN
                    SELECT RAISE(ABORT, 'forced assistant failure');
                END;
                """);
        }

        await using (var context = database.CreateContext())
        {
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                CreatePrepare(context, clock).ExecuteAsync(
                    new PrepareSessionRunRequest(projectContext, null, "rollback")));
        }

        await using var verificationContext = database.CreateContext();
        Assert.Empty(await verificationContext.Projects.ToListAsync());
        Assert.Empty(await verificationContext.Sessions.ToListAsync());
        Assert.Empty(await verificationContext.Messages.ToListAsync());
        Assert.Empty(await verificationContext.MessageParts.ToListAsync());
        Assert.Empty(await verificationContext.RunLeases.ToListAsync());
    }

    [Fact]
    public async Task Two_independent_contexts_cannot_prepare_the_same_session_concurrently()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/concurrency");
        SessionId sessionId;

        await using (var seedContext = database.CreateContext())
        {
            var project = await CreateRegisterProject(seedContext, clock).ExecuteAsync(projectContext);
            sessionId = (await CreateSessionUseCase(seedContext, clock).ExecuteAsync(project.Id)).Id;
        }

        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var firstTask = CreatePrepare(firstContext, clock).ExecuteAsync(
            new PrepareSessionRunRequest(projectContext, sessionId, "first concurrent"));
        var secondTask = CreatePrepare(secondContext, clock).ExecuteAsync(
            new PrepareSessionRunRequest(projectContext, sessionId, "second concurrent"));

        var outcomes = await Task.WhenAll(
            CaptureAsync(firstTask),
            CaptureAsync(secondTask));

        Assert.Single(outcomes, outcome => outcome.Result is not null);
        var rejectedOutcome = Assert.Single(outcomes, outcome => outcome.Exception is not null);
        Assert.NotNull(rejectedOutcome.Exception);
        Assert.Equal(
            SessionRunErrorCode.SessionAlreadyRunning,
            Assert.IsType<SessionRunException>(rejectedOutcome.Exception).Code);

        await using var verificationContext = database.CreateContext();
        Assert.Equal(1, await verificationContext.RunLeases.CountAsync());
        Assert.Equal(2, await verificationContext.Messages.CountAsync());
    }

    [Fact]
    public async Task Lease_release_requires_owner_and_returns_session_to_idle_atomically()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/release");
        PrepareSessionRunResult prepared;

        await using (var context = database.CreateContext())
        {
            prepared = await CreatePrepare(context, clock).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, null, "release"));
        }

        await using (var context = database.CreateContext())
        {
            var exception = await Assert.ThrowsAsync<SessionRunException>(() =>
                CreateEnd(context, clock).ExecuteAsync(prepared.Session.Id, RunLeaseId.New()));
            Assert.Equal(SessionRunErrorCode.RunLeaseOwnershipMismatch, exception.Code);
        }

        await using (var context = database.CreateContext())
        {
            await CreateEnd(context, clock).ExecuteAsync(
                prepared.Session.Id,
                prepared.RunLease.LeaseId);
        }

        await using var verificationContext = database.CreateContext();
        Assert.Empty(await verificationContext.RunLeases.ToListAsync());
        Assert.Equal(SessionStatus.Idle, (await verificationContext.Sessions.SingleAsync()).Status);
    }


    [Fact]
    public async Task Lease_can_only_be_renewed_by_its_owner()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/renew");
        PrepareSessionRunResult prepared;

        await using (var context = database.CreateContext())
        {
            prepared = await CreatePrepare(context, clock, TimeSpan.FromMinutes(1)).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, null, "renew"));
        }

        clock.UtcNow = InitialUtc.AddSeconds(30);
        await using (var context = database.CreateContext())
        {
            var exception = await Assert.ThrowsAsync<SessionRunException>(() =>
                CreateRenew(context, clock, TimeSpan.FromMinutes(5)).ExecuteAsync(
                    prepared.Session.Id,
                    RunLeaseId.New()));
            Assert.Equal(SessionRunErrorCode.RunLeaseOwnershipMismatch, exception.Code);
        }

        await using (var context = database.CreateContext())
        {
            var renewed = await CreateRenew(context, clock, TimeSpan.FromMinutes(5)).ExecuteAsync(
                prepared.Session.Id,
                prepared.RunLease.LeaseId);
            Assert.Equal(clock.UtcNow.AddMinutes(5), renewed.ExpiresAtUtc);
        }
    }

    [Fact]
    public async Task Expired_lease_recovers_partial_assistant_and_allows_a_new_run()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/recovery");
        PrepareSessionRunResult abandoned;

        await using (var context = database.CreateContext())
        {
            abandoned = await CreatePrepare(context, clock, TimeSpan.FromMinutes(1)).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, null, "abandoned"));
        }

        clock.UtcNow = InitialUtc.AddSeconds(30);
        await using (var context = database.CreateContext())
        {
            var assistant = await new MessageRepository(context).GetByIdAsync(abandoned.AssistantMessage.Id);
            var text = Assert.IsType<TextMessagePart>(Assert.Single(assistant!.Parts));
            text.ReplaceText("partial response", clock.UtcNow);
            await context.SaveChangesAsync();
        }

        clock.UtcNow = InitialUtc.AddMinutes(2);
        PrepareSessionRunResult recovered;
        await using (var context = database.CreateContext())
        {
            recovered = await CreatePrepare(context, clock, TimeSpan.FromMinutes(1)).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, abandoned.Session.Id, "after recovery"));
        }

        var recoveredAssistant = Assert.Single(
            recovered.OrderedPreviousHistory,
            message => message.Role == MessageRole.Assistant);
        Assert.Equal(MessageStatus.Failed, recoveredAssistant.Status);
        Assert.Equal("partial response", AssertText(recoveredAssistant));
        Assert.NotNull(recoveredAssistant.FailureReason);
        Assert.Equal(SessionStatus.Running, recovered.Session.Status);
        Assert.NotEqual(abandoned.RunLease.LeaseId, recovered.RunLease.LeaseId);
        Assert.Equal(3, recovered.UserMessage.Sequence);
        Assert.Equal(4, recovered.AssistantMessage.Sequence);

        await using var verificationContext = database.CreateContext();
        Assert.Equal(1, await verificationContext.RunLeases.CountAsync());
        Assert.Equal(4, await verificationContext.Messages.CountAsync());
    }

    [Fact]
    public async Task Valid_lease_is_never_recovered_or_mutated()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new MutableClock(InitialUtc);
        var projectContext = CreateProjectContext(ProjectId.New(), "/workspace/valid-lease");
        PrepareSessionRunResult active;

        await using (var context = database.CreateContext())
        {
            active = await CreatePrepare(context, clock, TimeSpan.FromMinutes(5)).ExecuteAsync(
                new PrepareSessionRunRequest(projectContext, null, "active"));
        }

        clock.UtcNow = InitialUtc.AddMinutes(1);
        await using (var context = database.CreateContext())
        {
            var exception = await Assert.ThrowsAsync<SessionRunException>(() =>
                CreatePrepare(context, clock, TimeSpan.FromMinutes(5)).ExecuteAsync(
                    new PrepareSessionRunRequest(projectContext, active.Session.Id, "must reject")));
            Assert.Equal(SessionRunErrorCode.SessionAlreadyRunning, exception.Code);
        }

        await using var verificationContext = database.CreateContext();
        var assistant = await new MessageRepository(verificationContext)
            .GetByIdAsync(active.AssistantMessage.Id);
        Assert.Equal(MessageStatus.Streaming, assistant!.Status);
        Assert.Null(assistant.FailureReason);
        Assert.Equal(SessionStatus.Running, (await verificationContext.Sessions.SingleAsync()).Status);
        Assert.Equal(active.RunLease.LeaseId, (await verificationContext.RunLeases.SingleAsync()).LeaseId);
    }

    private static RegisterProject CreateRegisterProject(
        AgentPulseDbContext context,
        IClock clock)
    {
        return new RegisterProject(
            new ProjectRepository(context),
            new UnitOfWork(context),
            clock);
    }

    private static CreateSession CreateSessionUseCase(
        AgentPulseDbContext context,
        IClock clock)
    {
        return new CreateSession(
            new ProjectRepository(context),
            new SessionRepository(context),
            new UnitOfWork(context),
            clock);
    }

    private static PrepareSessionRun CreatePrepare(
        AgentPulseDbContext context,
        IClock clock,
        TimeSpan? leaseDuration = null)
    {
        return new PrepareSessionRun(
            new ProjectRepository(context),
            new SessionRepository(context),
            new MessageRepository(context),
            new RunLeaseRepository(context),
            new UnitOfWork(context),
            clock,
            new SessionRunOptions
            {
                LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5),
            });
    }

    private static EndSessionRun CreateEnd(AgentPulseDbContext context, IClock clock)
    {
        return new EndSessionRun(
            new SessionRepository(context),
            new RunLeaseRepository(context),
            new UnitOfWork(context),
            clock);
    }


    private static RenewSessionRunLease CreateRenew(
        AgentPulseDbContext context,
        IClock clock,
        TimeSpan leaseDuration)
    {
        return new RenewSessionRunLease(
            new RunLeaseRepository(context),
            new UnitOfWork(context),
            clock,
            new SessionRunOptions { LeaseDuration = leaseDuration });
    }

    private static ProjectContext CreateProjectContext(ProjectId projectId, string root)
    {
        return new ProjectContext(
            root,
            root,
            false,
            null,
            ProjectPlatform.Linux,
            InitialUtc.Date,
            projectId);
    }

    private static string AssertText(Message message)
    {
        return Assert.IsType<TextMessagePart>(Assert.Single(message.Parts)).Text;
    }

    private static async Task<RunOutcome> CaptureAsync(Task<PrepareSessionRunResult> task)
    {
        try
        {
            return new RunOutcome(await task, null);
        }
        catch (Exception exception)
        {
            return new RunOutcome(null, exception);
        }
    }

    private sealed record RunOutcome(PrepareSessionRunResult? Result, Exception? Exception);

    private sealed class MutableClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }
}
