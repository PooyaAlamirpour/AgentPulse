using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Application.Tests.Permissions;

public sealed class MutationDefaultPermissionTests
{
    [Fact]
    public async Task Tool_specific_ask_overrides_global_allow_when_no_rule_matches()
    {
        var prompt = new RecordingPrompt(PermissionApprovalChoice.AllowOnce);
        var gate = CreateGate(prompt, new PermissionOptions());

        var result = await AuthorizeAsync(gate, new MutationPermissionTool());

        Assert.True(result.IsAllowed);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public async Task Existing_tool_without_default_metadata_keeps_global_allow()
    {
        var prompt = new RecordingPrompt();
        var gate = CreateGate(prompt, new PermissionOptions());

        var result = await AuthorizeAsync(gate, new ExistingPermissionTool());

        Assert.True(result.IsAllowed);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task Explicit_allow_rule_overrides_mutation_default_ask()
    {
        var prompt = new RecordingPrompt();
        var gate = CreateGate(
            prompt,
            new PermissionOptions
            {
                Rules =
                [
                    new PermissionRuleOptions
                    {
                        Tool = "write",
                        Operation = "write",
                        Target = "src/**",
                        Decision = "Allow",
                    },
                ],
            });

        var result = await AuthorizeAsync(gate, new MutationPermissionTool());

        Assert.True(result.IsAllowed);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task Explicit_deny_rule_overrides_mutation_default_ask()
    {
        var prompt = new RecordingPrompt(PermissionApprovalChoice.AllowOnce);
        var gate = CreateGate(
            prompt,
            new PermissionOptions
            {
                Rules =
                [
                    new PermissionRuleOptions
                    {
                        Tool = "write",
                        Operation = "write",
                        Target = "src/**",
                        Decision = "Deny",
                    },
                ],
            });

        var result = await AuthorizeAsync(gate, new MutationPermissionTool());

        Assert.False(result.IsAllowed);
        Assert.True(result.IsExplicitlyDenied);
        Assert.Equal(0, prompt.CallCount);
    }

    private static PermissionGate CreateGate(
        IPermissionApprovalPrompt prompt,
        PermissionOptions options) => new(
        new PermissionRuleEvaluator(),
        new EmptySessionStore(),
        new EmptyProjectStore(),
        prompt,
        options,
        NullLogger<PermissionGate>.Instance);

    private static async Task<PermissionAuthorizationResult> AuthorizeAsync(
        PermissionGate gate,
        IAgentTool tool)
    {
        using var document = JsonDocument.Parse("{}");
        return await gate.AuthorizeAsync(
            tool,
            document.RootElement,
            new AgentToolExecutionContext(Directory.GetCurrentDirectory()),
            SessionId.New(),
            ProjectId.New(),
            CancellationToken.None);
    }

    private sealed class MutationPermissionTool
        : IAgentTool, IPermissionAwareAgentTool, IAgentToolDefaultPermission
    {
        public string Name => "write";

        public string Description => "test";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public PermissionDecision DefaultPermissionDecision => PermissionDecision.Ask;

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(AgentToolResult.Success("unused"));

        public PermissionTargetResolution ResolvePermissionTarget(
            JsonElement arguments,
            AgentToolExecutionContext context) =>
            PermissionTargetResolution.Success("write", "src/Program.cs");
    }

    private sealed class ExistingPermissionTool : IAgentTool, IPermissionAwareAgentTool
    {
        public string Name => "read";

        public string Description => "test";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(AgentToolResult.Success("unused"));

        public PermissionTargetResolution ResolvePermissionTarget(
            JsonElement arguments,
            AgentToolExecutionContext context) =>
            PermissionTargetResolution.Success("read", "src/Program.cs");
    }

    private sealed class RecordingPrompt(params PermissionApprovalChoice[] choices)
        : IPermissionApprovalPrompt
    {
        private readonly Queue<PermissionApprovalChoice> _choices = new(choices);

        public bool IsInteractive => true;

        public int CallCount { get; private set; }

        public Task<PermissionApprovalChoice> RequestApprovalAsync(
            PermissionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_choices.Dequeue());
        }
    }

    private sealed class EmptySessionStore : ISessionPermissionStore
    {
        public Task<bool> ContainsAsync(
            SessionId sessionId,
            PermissionApproval approval,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task AddAsync(
            SessionId sessionId,
            PermissionApproval approval,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyProjectStore : IProjectPermissionStore
    {
        public Task<bool> ContainsAsync(
            ProjectId projectId,
            PermissionApproval approval,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task AddAsync(
            ProjectId projectId,
            PermissionApproval approval,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
