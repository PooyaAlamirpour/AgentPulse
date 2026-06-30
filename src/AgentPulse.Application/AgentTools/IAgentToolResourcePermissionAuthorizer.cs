using AgentPulse.Application.Permissions;

namespace AgentPulse.Application.AgentTools;

public interface IAgentToolResourcePermissionAuthorizer
{
    Task<PermissionAuthorizationResult> AuthorizeAsync(
        string operation,
        string target,
        string? description,
        CancellationToken cancellationToken);
}
