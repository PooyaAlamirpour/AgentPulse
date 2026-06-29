using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentTools;

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly IReadOnlyList<ChatModelToolDefinition> _definitions;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var dictionary = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Name);
            if (!dictionary.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException(
                    $"An agent tool named '{tool.Name}' is already registered.");
            }
        }

        _tools = dictionary;
        _definitions = dictionary.Values
            .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
            .Select(static tool => new ChatModelToolDefinition(
                tool.Name,
                tool.Description,
                tool.ParametersJsonSchema))
            .ToArray();
    }

    public bool TryGet(string name, out IAgentTool? tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _tools.TryGetValue(name, out tool);
    }

    public IReadOnlyList<ChatModelToolDefinition> GetDefinitions() => _definitions;
}
