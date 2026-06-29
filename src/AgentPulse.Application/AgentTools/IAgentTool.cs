using System.Text.Json;

namespace AgentPulse.Application.AgentTools;

public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    string ParametersJsonSchema { get; }

    Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken);
}
