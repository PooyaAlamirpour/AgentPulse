using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.AgentTools;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class DeferredPermissionContractTests
{
    [Fact]
    public void All_mutation_tools_expose_a_valid_deferred_permission_contract()
    {
        var service = CreateService();
        IAgentTool[] tools =
        [
            new WriteAgentTool(service),
            new EditAgentTool(service),
            new MultiEditAgentTool(service),
            CreatePatchTool(),
        ];

        var registry = new AgentToolRegistry(tools, requirePermissionMetadata: true);

        foreach (var name in new[] { "write", "edit", "multi_edit", "apply_patch" })
        {
            Assert.True(registry.TryGet(name, out var tool));
            Assert.NotNull(Assert.IsAssignableFrom<IDeferredPermissionAgentTool>(tool)
                .DeferredPermissionContract);
        }
    }

    [Fact]
    public async Task Deferred_contract_denies_execution_without_resource_authorizer()
    {
        using var workspace = new TemporaryWorkspace();
        var service = CreateService();
        var tool = new WriteAgentTool(service);
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"path":"blocked.txt","content":"unsafe"}""");

        var result = await tool.DeferredPermissionContract.ExecuteAsync(
            tool,
            document.RootElement,
            new AgentToolExecutionContext(workspace.Root),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Contains("Deferred permission authorization is not configured", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.PathOf("blocked.txt")));
    }
}
