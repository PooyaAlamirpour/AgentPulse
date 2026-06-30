using AgentPulse.Application.Permissions;

namespace AgentPulse.Application.AgentTools;

public interface IAgentToolDefaultPermission
{
    PermissionDecision DefaultPermissionDecision { get; }
}
