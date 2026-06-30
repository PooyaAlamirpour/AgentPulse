using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Workspaces;

namespace AgentPulse.Infrastructure.Tests.AgentTools;

public sealed class GlobAgentToolTests
{
    private const string NonInteractivePermissionError =
        "Permission approval is required, but the current run is non-interactive.";

    [Fact]
    public async Task Finds_sorted_matches_and_ignores_build_directories()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/B.cs", "");
        workspace.Write("src/A.cs", "");
        workspace.Write("src/readme.md", "");
        workspace.Write("bin/Hidden.cs", "");
        workspace.Write("obj/Hidden.cs", "");
        workspace.Write(".git/Hidden.cs", "");
        var tool = CreateTool();

        var result = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"**/*.cs\"}");

        Assert.True(result.Succeeded);
        Assert.Equal("src/A.cs\nsrc/B.cs", result.Output);
    }

    [Fact]
    public async Task No_match_invalid_pattern_limit_and_outside_base_are_handled()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.cs", "");
        workspace.Write("b.cs", "");
        var tool = new GlobAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxGlobResults = 1 });

        var none = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"**/*.json\"}");
        var invalid = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"[broken\"}");
        var limited = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"*.cs\",\"maxResults\":20}");
        var outside = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"*\",\"basePath\":\"..\"}");

        Assert.True(none.Succeeded);
        Assert.Equal("No files found.", none.Output);
        Assert.False(invalid.Succeeded);
        Assert.True(limited.Succeeded);
        Assert.Contains("truncated", limited.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(outside.Succeeded);
    }

    [Fact]
    public async Task Explicit_deny_continues_and_allowed_candidates_remain()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a-allowed/first.txt", "first");
        workspace.Write("b-denied/private.txt", "private");
        workspace.Write("c-allowed/last.txt", "last");
        var authorizer = new TestResourcePermissionAuthorizer(target =>
            target.StartsWith("b-denied/", StringComparison.Ordinal)
                ? ExplicitDeny("glob", target)
                : PermissionAuthorizationResult.Allow());

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"**/*\"}",
            authorizer);

        Assert.True(result.Succeeded);
        Assert.Equal("a-allowed/first.txt\nc-allowed/last.txt", result.Output);
        Assert.DoesNotContain("b-denied/private.txt", result.Output, StringComparison.Ordinal);
        Assert.Equal("2", result.Metadata["matches"]);
        Assert.Equal(
            ["a-allowed/first.txt", "b-denied/private.txt", "c-allowed/last.txt"],
            authorizer.Targets);
    }

    [Fact]
    public async Task Non_interactive_ask_fails_instead_of_returning_empty_success()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a-allowed/public.txt", "public");
        workspace.Write("b-restricted/private.txt", "private");
        workspace.Write("c-after/last.txt", "last");
        var authorizer = new TestResourcePermissionAuthorizer(target =>
            target.StartsWith("b-restricted/", StringComparison.Ordinal)
                ? NonInteractiveAskFailure("glob", target)
                : PermissionAuthorizationResult.Allow());

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"**/*\"}",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(NonInteractivePermissionError, result.Error);
        Assert.NotEqual("No files found.", result.Output);
        Assert.Empty(result.Output);
        Assert.Equal(
            ["a-allowed/public.txt", "b-restricted/private.txt"],
            authorizer.Targets);
    }

    [Fact]
    public async Task Infrastructure_failure_aborts_without_processing_later_candidates_or_partial_results()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a-allowed/public.txt", "public");
        workspace.Write("b-failure/private.txt", "private");
        workspace.Write("c-after/last.txt", "last");
        const string error = "Permission infrastructure failed while loading project approvals.";
        var authorizer = new TestResourcePermissionAuthorizer(target =>
            target.StartsWith("b-failure/", StringComparison.Ordinal)
                ? PermissionAuthorizationResult.Reject(AgentToolResult.Failure(error))
                : PermissionAuthorizationResult.Allow());

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"**/*\"}",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(error, result.Error);
        Assert.Empty(result.Output);
        Assert.Equal(
            ["a-allowed/public.txt", "b-failure/private.txt"],
            authorizer.Targets);
    }

    [Fact]
    public async Task Cancellation_during_resource_approval_propagates_and_stops_candidates()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a-restricted/private.txt", "private");
        workspace.Write("b-after/last.txt", "last");
        using var cancellation = new CancellationTokenSource();
        var authorizer = new TestResourcePermissionAuthorizer(target =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"**/*\"}",
            authorizer,
            cancellationToken: cancellation.Token));

        Assert.Equal(["a-restricted/private.txt"], authorizer.Targets);
    }

    [Fact]
    public async Task Missing_resource_authorizer_fails_the_tool_call()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/public.txt", "public");

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"**/*\"}",
            authorizer: null,
            useResourceAuthorizer: false);

        Assert.False(result.Succeeded);
        Assert.Contains("Resource-level permission authorization is unavailable", result.Error, StringComparison.Ordinal);
        Assert.NotEqual("No files found.", result.Output);
    }

    private static PermissionAuthorizationResult ExplicitDeny(string toolName, string target) =>
        PermissionAuthorizationResult.Reject(
            AgentToolResult.Failure($"Permission denied for tool '{toolName}' on '{target}'."),
            new PermissionEvaluationResult(
                PermissionDecision.Deny,
                null,
                toolName,
                "search",
                target,
                "The matching permission rule denied the resource.",
                PermissionRuleSource.Configuration,
                null),
            PermissionAuthorizationStatus.ExplicitlyDenied);

    private static PermissionAuthorizationResult NonInteractiveAskFailure(
        string toolName,
        string target) => PermissionAuthorizationResult.Reject(
            AgentToolResult.Failure(NonInteractivePermissionError),
            new PermissionEvaluationResult(
                PermissionDecision.Deny,
                null,
                toolName,
                "search",
                target,
                "The permission request required approval in a non-interactive run.",
                PermissionRuleSource.NonInteractivePolicy,
                null),
            PermissionAuthorizationStatus.ApprovalUnavailable);

    private static GlobAgentTool CreateTool() => new(
        new WorkspacePathResolver(),
        new AgentToolOptions());

    private static async Task<AgentToolResult> ExecuteAsync(
        GlobAgentTool tool,
        string root,
        string json,
        IAgentToolResourcePermissionAuthorizer? authorizer = null,
        bool useResourceAuthorizer = true,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(
            document.RootElement,
            new AgentToolExecutionContext(
                root,
                useResourceAuthorizer
                    ? authorizer ?? new TestResourcePermissionAuthorizer()
                    : null),
            cancellationToken);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agentpulse-glob-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relative, string content)
        {
            var path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
