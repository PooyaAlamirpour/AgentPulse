using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Domain.Tests.Sessions;

public sealed class SessionTests
{
    [Fact]
    public void Session_starts_idle_and_guards_state_transitions()
    {
        var createdAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = new Session(SessionId.New(), ProjectId.New(), createdAtUtc);

        Assert.Equal(SessionStatus.Idle, session.Status);

        session.Start(createdAtUtc.AddMinutes(1));
        Assert.Equal(SessionStatus.Running, session.Status);
        Assert.Throws<InvalidOperationException>(() => session.Start(createdAtUtc.AddMinutes(2)));

        session.Stop(createdAtUtc.AddMinutes(3));
        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Throws<InvalidOperationException>(() => session.Stop(createdAtUtc.AddMinutes(4)));
    }

    [Fact]
    public void Failed_timestamp_validation_does_not_mutate_status()
    {
        var createdAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var session = new Session(SessionId.New(), ProjectId.New(), createdAtUtc);

        Assert.Throws<ArgumentException>(() => session.Start(DateTime.Now));

        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Equal(createdAtUtc, session.UpdatedAtUtc);
    }
}
