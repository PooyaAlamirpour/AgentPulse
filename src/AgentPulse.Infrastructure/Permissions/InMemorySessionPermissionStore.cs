using System.Collections.Concurrent;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Infrastructure.Permissions;

public sealed class InMemorySessionPermissionStore : ISessionPermissionStore
{
    private readonly ConcurrentDictionary<SessionId, ConcurrentDictionary<string, byte>> _approvals = new();

    public Task<bool> ContainsAsync(
        SessionId sessionId,
        PermissionApproval approval,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(approval);
        var exists = _approvals.TryGetValue(sessionId, out var sessionApprovals) &&
                     sessionApprovals.ContainsKey(CreateKey(approval));
        return Task.FromResult(exists);
    }

    public Task AddAsync(
        SessionId sessionId,
        PermissionApproval approval,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(approval);
        var sessionApprovals = _approvals.GetOrAdd(
            sessionId,
            static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        sessionApprovals.TryAdd(CreateKey(approval), 0);
        return Task.CompletedTask;
    }

    private static string CreateKey(PermissionApproval approval)
    {
        var target = OperatingSystem.IsWindows()
            ? approval.Target.ToUpperInvariant()
            : approval.Target;
        return string.Join('\u001F', approval.ToolName, approval.Operation, target);
    }
}
