using System.Text.Json;

namespace AgentPulse.Application.AgentTools;

public interface IDeferredPermissionAgentTool
{
    IDeferredPermissionExecutionContract DeferredPermissionContract { get; }
}

public interface IDeferredPermissionExecutionContract
{
    Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken);
}
