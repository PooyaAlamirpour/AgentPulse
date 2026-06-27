using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Tests.SessionRuns;

public sealed class SessionRunUseCaseTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Continue_session_returns_matching_session()
    {
        var projectId = ProjectId.New();
        var session = new Session(SessionId.New(), projectId, UtcNow);
        var useCase = new ContinueSession(new StubSessionRepository(session));

        var result = await useCase.ExecuteAsync(projectId, session.Id);

        Assert.Same(session, result);
    }

    [Fact]
    public async Task Continue_session_rejects_missing_or_foreign_session()
    {
        var projectId = ProjectId.New();
        var session = new Session(SessionId.New(), projectId, UtcNow);

        var mismatch = await Assert.ThrowsAsync<SessionRunException>(() =>
            new ContinueSession(new StubSessionRepository(session))
                .ExecuteAsync(ProjectId.New(), session.Id));
        Assert.Equal(SessionRunErrorCode.SessionProjectMismatch, mismatch.Code);

        var missing = await Assert.ThrowsAsync<SessionRunException>(() =>
            new ContinueSession(new StubSessionRepository(null))
                .ExecuteAsync(projectId, session.Id));
        Assert.Equal(SessionRunErrorCode.SessionNotFound, missing.Code);
    }

    [Fact]
    public async Task Prepare_rejects_blank_prompt_before_opening_a_transaction()
    {
        var useCase = new PrepareSessionRun(
            null!,
            null!,
            null!,
            null!,
            null!,
            new StubClock(UtcNow),
            new SessionRunOptions());
        var projectContext = new ProjectContext(
            "/workspace/project",
            "/workspace/project",
            false,
            null,
            ProjectPlatform.Linux,
            UtcNow.Date,
            ProjectId.New());

        var exception = await Assert.ThrowsAsync<SessionRunException>(() =>
            useCase.ExecuteAsync(new PrepareSessionRunRequest(projectContext, null, " ")));

        Assert.Equal(SessionRunErrorCode.InvalidUserPrompt, exception.Code);
    }

    [Fact]
    public void Lease_options_require_positive_duration()
    {
        var options = new SessionRunOptions { LeaseDuration = TimeSpan.Zero };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private sealed class StubClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class StubSessionRepository(Session? session) : ISessionRepository
    {
        public Task<Session?> GetByIdAsync(
            SessionId id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(session?.Id == id ? session : null);
        }

        public Task<IReadOnlyList<Session>> ListByProjectIdAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            if (session is not null && session.ProjectId == projectId)
            {
                IReadOnlyList<Session> matching = [session];
                return Task.FromResult(matching);
            }

            return Task.FromResult<IReadOnlyList<Session>>([]);
        }

        public Task AddAsync(Session value, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Remove(Session value)
        {
            throw new NotSupportedException();
        }
    }
}
