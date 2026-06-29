using System.Text.Json;
using AgentPulse.Application.AgentTools;

namespace AgentPulse.Application.Tests.AgentTools;

public sealed class AgentToolRegistryTests
{
    [Fact]
    public void Registered_tool_can_be_found_and_described()
    {
        var tool = new StubTool("read");
        var registry = new AgentToolRegistry([tool]);

        var found = registry.TryGet("read", out var resolved);
        var definition = Assert.Single(registry.GetDefinitions());

        Assert.True(found);
        Assert.Same(tool, resolved);
        Assert.Equal("read", definition.Name);
        Assert.Equal(tool.Description, definition.Description);
        Assert.Equal(tool.ParametersJsonSchema, definition.ParametersJsonSchema);
    }

    [Fact]
    public void Unknown_tool_is_not_found()
    {
        var registry = new AgentToolRegistry([new StubTool("read")]);

        Assert.False(registry.TryGet("missing", out var tool));
        Assert.Null(tool);
    }

    [Fact]
    public void Duplicate_name_is_rejected_with_ordinal_comparison()
    {
        Assert.Throws<InvalidOperationException>(() => new AgentToolRegistry(
            [new StubTool("read"), new StubTool("read")]));

        var registry = new AgentToolRegistry(
            [new StubTool("read"), new StubTool("Read")]);
        Assert.Equal(2, registry.GetDefinitions().Count);
    }

    private sealed class StubTool(string name) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "description";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(AgentToolResult.Success("ok"));
        }
    }
}
