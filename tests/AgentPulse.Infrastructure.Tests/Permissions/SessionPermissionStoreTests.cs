using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Permissions;

namespace AgentPulse.Infrastructure.Tests.Permissions;

public sealed class SessionPermissionStoreTests
{
    [Fact]
    public async Task Approval_is_session_scoped_and_duplicate_safe()
    {
        var store = new InMemorySessionPermissionStore();
        var session = SessionId.New();
        var otherSession = SessionId.New();
        var approval = PermissionApproval.Create("read", "read", "src/Program.cs");

        await store.AddAsync(session, approval, CancellationToken.None);
        await store.AddAsync(session, approval, CancellationToken.None);

        Assert.True(await store.ContainsAsync(session, approval, CancellationToken.None));
        Assert.False(await store.ContainsAsync(otherSession, approval, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_adds_are_thread_safe()
    {
        var store = new InMemorySessionPermissionStore();
        var session = SessionId.New();
        var approvals = Enumerable.Range(0, 100)
            .Select(index => PermissionApproval.Create("read", "read", $"src/File{index}.cs"))
            .ToArray();

        await Task.WhenAll(approvals.Select(approval =>
            store.AddAsync(session, approval, CancellationToken.None)));

        foreach (var approval in approvals)
        {
            Assert.True(await store.ContainsAsync(session, approval, CancellationToken.None));
        }
    }
}
