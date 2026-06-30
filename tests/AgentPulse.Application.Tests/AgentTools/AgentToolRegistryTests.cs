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

    [Fact]
    public void Strict_registration_rejects_tool_without_permission_metadata()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AgentToolRegistry([new StubTool("unclassified")], requirePermissionMetadata: true));

        Assert.Contains("Permission metadata is not defined", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_registration_accepts_classified_tool()
    {
        var registry = new AgentToolRegistry(
            [new ClassifiedStubTool("read")],
            requirePermissionMetadata: true);

        Assert.True(registry.TryGet("read", out _));
    }

    [Fact]
    public void Deferred_permission_tool_without_execution_contract_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AgentToolRegistry([new DeferredStubTool(null!)]));

        Assert.Contains(
            "Deferred permission authorization is not configured",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Deferred_permission_tool_with_execution_contract_is_registered()
    {
        var contract = new DeferredContractStub();
        var registry = new AgentToolRegistry([new DeferredStubTool(contract)]);

        Assert.True(registry.TryGet("deferred", out var tool));
        Assert.Same(contract, Assert.IsAssignableFrom<IDeferredPermissionAgentTool>(tool).DeferredPermissionContract);
    }

    private sealed class DeferredStubTool(IDeferredPermissionExecutionContract contract)
        : IAgentTool, IDeferredPermissionAgentTool
    {
        public string Name => "deferred";

        public string Description => "description";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public IDeferredPermissionExecutionContract DeferredPermissionContract { get; } = contract;

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(AgentToolResult.Success("ok"));
    }

    private sealed class DeferredContractStub : IDeferredPermissionExecutionContract
    {
        public Task<AgentToolResult> ExecuteAsync(
            IAgentTool tool,
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(AgentToolResult.Success("ok"));
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

    private sealed class ClassifiedStubTool(string name) : IAgentTool, IPermissionAwareAgentTool
    {
        public string Name { get; } = name;

        public string Description => "description";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(AgentToolResult.Success("ok"));

        public PermissionTargetResolution ResolvePermissionTarget(
            JsonElement arguments,
            AgentToolExecutionContext context) => PermissionTargetResolution.Success("read", "file.txt");
    }
}
