using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentTools;

public interface IAgentToolRegistry
{
    bool TryGet(string name, out IAgentTool? tool);

    IReadOnlyList<ChatModelToolDefinition> GetDefinitions();
}
