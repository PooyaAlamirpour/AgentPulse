using AgentPulse.Domain.Projects;

namespace AgentPulse.Application.Permissions;

public interface IProjectPermissionStore
{
    Task<bool> ContainsAsync(
        ProjectId projectId,
        PermissionApproval approval,
        CancellationToken cancellationToken);

    Task AddAsync(
        ProjectId projectId,
        PermissionApproval approval,
        CancellationToken cancellationToken);
}
