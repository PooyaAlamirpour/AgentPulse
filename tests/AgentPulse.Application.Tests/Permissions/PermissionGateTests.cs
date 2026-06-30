using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Application.Tests.Permissions;

public sealed class PermissionGateTests
{
    [Fact]
    public async Task Allow_once_applies_only_to_the_current_request()
    {
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce, PermissionApprovalChoice.Deny);
        var gate = CreateGate(prompt, AskOptions());
        var context = CreateContext();
        var session = SessionId.New();
        var project = ProjectId.New();

        var first = await AuthorizeAsync(gate, context, session, project);
        var second = await AuthorizeAsync(gate, context, session, project);

        Assert.True(first.IsAllowed);
        Assert.Equal(PermissionRuleSource.OneTimeApproval, first.Resolution?.RuleSource);
        Assert.Equal(PermissionScope.Once, first.Resolution?.PersistenceScope);
        Assert.False(second.IsAllowed);
        Assert.True(second.IsExplicitlyDenied);
        Assert.Equal(PermissionAuthorizationStatus.ExplicitlyDenied, second.Status);
        Assert.Equal(PermissionRuleSource.UserDecision, second.Resolution?.RuleSource);
        Assert.Equal(2, prompt.CallCount);
    }

    [Fact]
    public async Task Session_approval_applies_only_to_the_same_session()
    {
        var prompt = new QueuePrompt(
            true,
            PermissionApprovalChoice.AllowSession,
            PermissionApprovalChoice.Deny);
        var gate = CreateGate(prompt, AskOptions());
        var context = CreateContext();
        var project = ProjectId.New();
        var firstSession = SessionId.New();

        var stored = await AuthorizeAsync(gate, context, firstSession, project);
        var reused = await AuthorizeAsync(gate, context, firstSession, project);
        Assert.True(stored.IsAllowed);
        Assert.True(reused.IsAllowed);
        Assert.Equal(PermissionRuleSource.SessionApproval, reused.Resolution?.RuleSource);
        Assert.Equal(PermissionScope.Session, reused.Resolution?.PersistenceScope);
        Assert.False((await AuthorizeAsync(gate, context, SessionId.New(), project)).IsAllowed);
        Assert.Equal(2, prompt.CallCount);
    }

    [Fact]
    public async Task Project_approval_applies_only_to_the_same_project()
    {
        var prompt = new QueuePrompt(
            true,
            PermissionApprovalChoice.AllowProject,
            PermissionApprovalChoice.Deny);
        var gate = CreateGate(prompt, AskOptions());
        var context = CreateContext();
        var firstProject = ProjectId.New();

        var stored = await AuthorizeAsync(gate, context, SessionId.New(), firstProject);
        var reused = await AuthorizeAsync(gate, context, SessionId.New(), firstProject);
        Assert.True(stored.IsAllowed);
        Assert.True(reused.IsAllowed);
        Assert.Equal(PermissionRuleSource.ProjectApproval, reused.Resolution?.RuleSource);
        Assert.Equal(PermissionScope.Project, reused.Resolution?.PersistenceScope);
        Assert.False((await AuthorizeAsync(gate, context, SessionId.New(), ProjectId.New())).IsAllowed);
        Assert.Equal(2, prompt.CallCount);
    }

    [Fact]
    public async Task Explicit_deny_is_not_overridden_by_approval()
    {
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowProject);
        var sessionStore = new MemorySessionStore();
        var projectStore = new MemoryProjectStore();
        var session = SessionId.New();
        var project = ProjectId.New();
        var approval = PermissionApproval.Create("read", "read", "src/Program.cs");
        await sessionStore.AddAsync(session, approval, CancellationToken.None);
        await projectStore.AddAsync(project, approval, CancellationToken.None);
        var gate = CreateGate(prompt, new PermissionOptions
        {
            Rules =
            [
                new PermissionRuleOptions
                {
                    Tool = "read",
                    Operation = "read",
                    Target = "src/Program.cs",
                    Decision = "Deny",
                },
            ],
        }, sessionStore, projectStore);

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            session,
            project);

        Assert.False(result.IsAllowed);
        Assert.True(result.IsExplicitlyDenied);
        Assert.Equal(PermissionAuthorizationStatus.ExplicitlyDenied, result.Status);
        Assert.Equal(0, prompt.CallCount);
        Assert.Contains("Permission denied", result.Failure?.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PermissionScope.Once)]
    [InlineData(PermissionScope.Session)]
    [InlineData(PermissionScope.Project)]
    public async Task Non_interactive_ask_denies_without_prompting(PermissionScope scope)
    {
        var prompt = new QueuePrompt(false);
        var gate = CreateGate(prompt, AskOptions(scope));

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New());

        Assert.False(result.IsAllowed);
        Assert.False(result.IsExplicitlyDenied);
        Assert.Equal(PermissionAuthorizationStatus.ApprovalUnavailable, result.Status);
        Assert.Equal(0, prompt.CallCount);
        Assert.Equal(
            "Permission approval is required, but the current run is non-interactive.",
            result.Failure?.Error);
    }

    [Fact]
    public async Task Approval_unavailable_is_not_treated_as_explicit_deny()
    {
        var gate = CreateGate(
            new QueuePrompt(true, PermissionApprovalChoice.Unavailable),
            AskOptions());

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New());

        Assert.False(result.IsAllowed);
        Assert.False(result.IsExplicitlyDenied);
        Assert.Equal(PermissionAuthorizationStatus.ApprovalUnavailable, result.Status);
        Assert.Equal(
            "Permission approval is unavailable because the input stream reached EOF.",
            result.Failure?.Error);
    }

    [Fact]
    public async Task Permission_evaluator_failure_is_classified_as_infrastructure_failure()
    {
        var gate = CreateGate(
            new QueuePrompt(true),
            AskOptions(),
            evaluator: new ThrowingEvaluator());

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New());

        Assert.False(result.IsAllowed);
        Assert.False(result.IsExplicitlyDenied);
        Assert.Equal(PermissionAuthorizationStatus.InfrastructureFailure, result.Status);
        Assert.Equal("Permission evaluation failed: evaluator unavailable", result.Failure?.Error);
    }

    [Fact]
    public async Task Permission_store_failure_is_classified_as_infrastructure_failure()
    {
        var gate = CreateGate(
            new QueuePrompt(true),
            AskOptions(),
            sessionStore: new ThrowingSessionStore());

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New());

        Assert.False(result.IsAllowed);
        Assert.False(result.IsExplicitlyDenied);
        Assert.Equal(PermissionAuthorizationStatus.InfrastructureFailure, result.Status);
        Assert.Equal("Session permission data could not be loaded.", result.Failure?.Error);
    }

    [Fact]
    public async Task Unclassified_tool_is_denied_by_default_without_execution_metadata()
    {
        var prompt = new QueuePrompt(true);
        var gate = CreateGate(prompt, new PermissionOptions());
        using var document = JsonDocument.Parse("{}");

        var result = await gate.AuthorizeAsync(
            new UnclassifiedTool(),
            document.RootElement,
            CreateContext(),
            SessionId.New(),
            ProjectId.New(),
            CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Equal(
            "Permission metadata is not defined for tool 'unclassified'. Execution was denied.",
            result.Failure?.Error);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task Default_allow_applies_to_classified_tool_only()
    {
        var prompt = new QueuePrompt(true);
        var gate = CreateGate(prompt, new PermissionOptions());

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New());

        Assert.True(result.IsAllowed);
        Assert.Equal(PermissionRuleSource.DefaultPolicy, result.Resolution?.RuleSource);
        Assert.Equal(0, prompt.CallCount);
    }

    [Theory]
    [InlineData(PermissionApprovalChoice.AllowSession)]
    [InlineData(PermissionApprovalChoice.AllowProject)]
    public async Task Once_scope_rejects_wider_approval_and_does_not_persist_it(
        PermissionApprovalChoice choice)
    {
        var prompt = new QueuePrompt(true, choice);
        var sessionStore = new MemorySessionStore();
        var projectStore = new MemoryProjectStore();
        var gate = CreateGate(
            prompt,
            AskOptions(PermissionScope.Once),
            sessionStore,
            projectStore: projectStore);

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New());

        Assert.False(result.IsAllowed);
        Assert.False(result.IsExplicitlyDenied);
        Assert.Equal(PermissionAuthorizationStatus.InvalidApproval, result.Status);
        Assert.Contains("exceeds the configured Once scope", result.Failure?.Error, StringComparison.Ordinal);
        Assert.Equal(0, sessionStore.Count);
        Assert.Equal(0, projectStore.Count);
    }

    [Fact]
    public async Task Session_scope_rejects_project_approval_and_does_not_persist_it()
    {
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowProject);
        var projectStore = new MemoryProjectStore();
        var gate = CreateGate(
            prompt,
            AskOptions(PermissionScope.Session),
            projectStore: projectStore);

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New());

        Assert.False(result.IsAllowed);
        Assert.Contains("exceeds the configured Session scope", result.Failure?.Error, StringComparison.Ordinal);
        Assert.Equal(0, projectStore.Count);
    }

    [Fact]
    public async Task Stale_project_approval_is_ignored_when_rule_scope_is_lowered()
    {
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.Deny);
        var projectStore = new MemoryProjectStore();
        var project = ProjectId.New();
        await projectStore.AddAsync(
            project,
            PermissionApproval.Create("read", "read", "src/Program.cs"),
            CancellationToken.None);
        var gate = CreateGate(
            prompt,
            AskOptions(PermissionScope.Once),
            projectStore: projectStore);

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            project);

        Assert.False(result.IsAllowed);
        Assert.Equal(PermissionRuleSource.UserDecision, result.Resolution?.RuleSource);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public async Task Stale_session_approval_is_ignored_when_rule_scope_is_once()
    {
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.Deny);
        var sessionStore = new MemorySessionStore();
        var session = SessionId.New();
        await sessionStore.AddAsync(
            session,
            PermissionApproval.Create("read", "read", "src/Program.cs"),
            CancellationToken.None);
        var gate = CreateGate(
            prompt,
            AskOptions(PermissionScope.Once),
            sessionStore);

        var result = await AuthorizeAsync(
            gate,
            CreateContext(),
            session,
            ProjectId.New());

        Assert.False(result.IsAllowed);
        Assert.Equal(PermissionRuleSource.UserDecision, result.Resolution?.RuleSource);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public async Task Cancellation_from_approval_prompt_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var gate = CreateGate(new CancellingPrompt(), AskOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuthorizeAsync(
            gate,
            CreateContext(),
            SessionId.New(),
            ProjectId.New(),
            cancellation.Token));
    }

    private static PermissionGate CreateGate(
        IPermissionApprovalPrompt prompt,
        PermissionOptions options,
        ISessionPermissionStore? sessionStore = null,
        IProjectPermissionStore? projectStore = null,
        IPermissionRuleEvaluator? evaluator = null)
    {
        return new PermissionGate(
            evaluator ?? new PermissionRuleEvaluator(),
            sessionStore ?? new MemorySessionStore(),
            projectStore ?? new MemoryProjectStore(),
            prompt,
            options,
            NullLogger<PermissionGate>.Instance);
    }

    private static PermissionOptions AskOptions(
        PermissionScope scope = PermissionScope.Project) => new()
    {
        Rules =
        [
            new PermissionRuleOptions
            {
                Tool = "read",
                Operation = "read",
                Target = "src/**",
                Decision = "Ask",
                Scope = scope.ToString(),
            },
        ],
    };

    private static AgentToolExecutionContext CreateContext()
    {
        return new AgentToolExecutionContext(Directory.GetCurrentDirectory());
    }

    private static Task<PermissionAuthorizationResult> AuthorizeAsync(
        PermissionGate gate,
        AgentToolExecutionContext context,
        SessionId sessionId,
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse("{}");
        return gate.AuthorizeAsync(
            new PermissionTool(),
            document.RootElement.Clone(),
            context,
            sessionId,
            projectId,
            cancellationToken);
    }

    private sealed class PermissionTool : IAgentTool, IPermissionAwareAgentTool
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

    private sealed class UnclassifiedTool : IAgentTool
    {
        public string Name => "unclassified";

        public string Description => "test";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(AgentToolResult.Success("unused"));
    }

    private sealed class QueuePrompt(bool isInteractive, params PermissionApprovalChoice[] choices)
        : IPermissionApprovalPrompt
    {
        private readonly Queue<PermissionApprovalChoice> _choices = new(choices);

        public bool IsInteractive { get; } = isInteractive;

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

    private sealed class CancellingPrompt : IPermissionApprovalPrompt
    {
        public bool IsInteractive => true;

        public Task<PermissionApprovalChoice> RequestApprovalAsync(
            PermissionRequest request,
            CancellationToken cancellationToken) => Task.FromCanceled<PermissionApprovalChoice>(cancellationToken);
    }

    private sealed class ThrowingEvaluator : IPermissionRuleEvaluator
    {
        public PermissionEvaluationResult Evaluate(
            PermissionRequest request,
            IReadOnlyCollection<PermissionRule> rules,
            PermissionDecision defaultDecision) => throw new InvalidOperationException("evaluator unavailable");
    }

    private sealed class ThrowingSessionStore : ISessionPermissionStore
    {
        public Task<bool> ContainsAsync(
            SessionId sessionId,
            PermissionApproval approval,
            CancellationToken cancellationToken) => throw new PermissionStoreException(
                "Session permission data could not be loaded.");

        public Task AddAsync(
            SessionId sessionId,
            PermissionApproval approval,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemorySessionStore : ISessionPermissionStore
    {
        private readonly HashSet<(SessionId SessionId, PermissionApproval Approval)> _items = [];

        public int Count => _items.Count;

        public Task<bool> ContainsAsync(
            SessionId sessionId,
            PermissionApproval approval,
            CancellationToken cancellationToken) =>
            Task.FromResult(_items.Contains((sessionId, approval)));

        public Task AddAsync(
            SessionId sessionId,
            PermissionApproval approval,
            CancellationToken cancellationToken)
        {
            _items.Add((sessionId, approval));
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryProjectStore : IProjectPermissionStore
    {
        private readonly HashSet<(ProjectId ProjectId, PermissionApproval Approval)> _items = [];

        public int Count => _items.Count;

        public Task<bool> ContainsAsync(
            ProjectId projectId,
            PermissionApproval approval,
            CancellationToken cancellationToken) =>
            Task.FromResult(_items.Contains((projectId, approval)));

        public Task AddAsync(
            ProjectId projectId,
            PermissionApproval approval,
            CancellationToken cancellationToken)
        {
            _items.Add((projectId, approval));
            return Task.CompletedTask;
        }
    }
}
