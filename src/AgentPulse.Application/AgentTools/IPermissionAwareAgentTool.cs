using System.Text.Json;

namespace AgentPulse.Application.AgentTools;

public interface IPermissionAwareAgentTool
{
    PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context);
}
