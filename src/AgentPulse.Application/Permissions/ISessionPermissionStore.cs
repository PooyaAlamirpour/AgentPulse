using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Permissions;

public interface ISessionPermissionStore
{
    Task<bool> ContainsAsync(
        SessionId sessionId,
        PermissionApproval approval,
        CancellationToken cancellationToken);

    Task AddAsync(
        SessionId sessionId,
        PermissionApproval approval,
        CancellationToken cancellationToken);
}
