using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Workspaces;

namespace AgentPulse.Infrastructure.Tests.AgentTools;

public sealed class GrepAgentToolTests
{
    private const string NonInteractivePermissionError =
        "Permission approval is required, but the current run is non-interactive.";

    [Fact]
    public async Task Finds_text_with_path_line_number_and_case_control()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("src/a.cs", "Alpha\nbeta\nALPHA\n");
        var tool = CreateTool();

        var insensitive = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"pattern\":\"alpha\",\"include\":\"**/*.cs\"}");
        var sensitive = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"pattern\":\"Alpha\",\"caseSensitive\":true}");

        Assert.True(insensitive.Succeeded);
        Assert.Contains("src/a.cs:1: Alpha", insensitive.Output, StringComparison.Ordinal);
        Assert.Contains("src/a.cs:3: ALPHA", insensitive.Output, StringComparison.Ordinal);
        Assert.True(sensitive.Succeeded);
        Assert.Contains("src/a.cs:1: Alpha", sensitive.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA", sensitive.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_regex_binary_file_limit_and_outside_path_are_handled()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a.txt", "hit\nhit\n");
        workspace.WriteBytes("binary.bin", [0, 1, 2, 3]);
        var tool = new GrepAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxGrepResults = 1 });

        var invalid = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"[\"}");
        var limited = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"hit\",\"maxResults\":20}");
        var binary = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"\\u0000\"}");
        var outside = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"x\",\"basePath\":\"..\"}");

        Assert.False(invalid.Succeeded);
        Assert.True(limited.Succeeded);
        Assert.Contains("a.txt:1", limited.Output, StringComparison.Ordinal);
        Assert.Contains("truncated", limited.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(binary.Succeeded);
        Assert.Equal("No matches found.", binary.Output);
        Assert.False(outside.Succeeded);
    }

    [Fact]
    public async Task Include_pattern_filters_files()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a.cs", "needle");
        workspace.WriteText("a.md", "needle");
        var tool = CreateTool();

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"pattern\":\"needle\",\"include\":\"*.cs\"}");

        Assert.Contains("a.cs:1", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("a.md", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_deny_skips_content_and_allowed_candidates_continue()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a-allowed/first.txt", "shared-needle first");
        workspace.WriteText("b-denied/private.txt", "shared-needle private-secret");
        workspace.WriteText("c-allowed/last.txt", "shared-needle last");
        var deniedPath = Path.Combine(workspace.Root, "b-denied", "private.txt");
        var authorizer = new TestResourcePermissionAuthorizer(target =>
        {
            if (!target.StartsWith("b-denied/", StringComparison.Ordinal))
            {
                return PermissionAuthorizationResult.Allow();
            }

            File.Delete(deniedPath);
            return ExplicitDeny("grep", target);
        });

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"shared-needle\"}",
            authorizer);

        Assert.True(result.Succeeded);
        Assert.Contains("a-allowed/first.txt:1", result.Output, StringComparison.Ordinal);
        Assert.Contains("c-allowed/last.txt:1", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("b-denied/private.txt", result.Output, StringComparison.Ordinal);
        Assert.Equal("2", result.Metadata["matches"]);
        Assert.Equal(
            ["a-allowed/first.txt", "b-denied/private.txt", "c-allowed/last.txt"],
            authorizer.Targets);
    }

    [Fact]
    public async Task Non_interactive_ask_fails_before_file_content_is_searched()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a-restricted/private.txt", "shared-needle private-secret");
        workspace.WriteText("b-after/last.txt", "shared-needle last");
        var restrictedPath = Path.Combine(workspace.Root, "a-restricted", "private.txt");
        var authorizer = new TestResourcePermissionAuthorizer(target =>
        {
            if (!target.StartsWith("a-restricted/", StringComparison.Ordinal))
            {
                return PermissionAuthorizationResult.Allow();
            }

            File.Delete(restrictedPath);
            return NonInteractiveAskFailure("grep", target);
        });

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"shared-needle\"}",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(NonInteractivePermissionError, result.Error);
        Assert.NotEqual("No matches found.", result.Output);
        Assert.Empty(result.Output);
        Assert.Equal(["a-restricted/private.txt"], authorizer.Targets);
    }

    [Fact]
    public async Task Infrastructure_failure_aborts_without_reading_later_candidates_or_returning_partial_matches()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a-allowed/public.txt", "shared-needle public");
        workspace.WriteText("b-failure/private.txt", "shared-needle private");
        workspace.WriteText("c-after/last.txt", "shared-needle last");
        const string error = "Permission infrastructure failed while loading session approvals.";
        var authorizer = new TestResourcePermissionAuthorizer(target =>
            target.StartsWith("b-failure/", StringComparison.Ordinal)
                ? PermissionAuthorizationResult.Reject(AgentToolResult.Failure(error))
                : PermissionAuthorizationResult.Allow());

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"shared-needle\"}",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(error, result.Error);
        Assert.Empty(result.Output);
        Assert.Equal(
            ["a-allowed/public.txt", "b-failure/private.txt"],
            authorizer.Targets);
    }

    [Fact]
    public async Task Cancellation_during_resource_approval_propagates_before_file_content_access()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a-restricted/private.txt", "shared-needle private-secret");
        workspace.WriteText("b-after/last.txt", "shared-needle last");
        using var cancellation = new CancellationTokenSource();
        var authorizer = new TestResourcePermissionAuthorizer(target =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"shared-needle\"}",
            authorizer,
            cancellationToken: cancellation.Token));

        Assert.Equal(["a-restricted/private.txt"], authorizer.Targets);
    }

    [Fact]
    public async Task Missing_resource_authorizer_fails_the_tool_call()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("src/public.txt", "shared-needle public");

        var result = await ExecuteAsync(
            CreateTool(),
            workspace.Root,
            "{\"pattern\":\"shared-needle\"}",
            authorizer: null,
            useResourceAuthorizer: false);

        Assert.False(result.Succeeded);
        Assert.Contains("Resource-level permission authorization is unavailable", result.Error, StringComparison.Ordinal);
        Assert.NotEqual("No matches found.", result.Output);
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

    private static GrepAgentTool CreateTool() => new(
        new WorkspacePathResolver(),
        new AgentToolOptions());

    private static async Task<AgentToolResult> ExecuteAsync(
        GrepAgentTool tool,
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
                $"agentpulse-grep-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WriteText(string relative, string content)
        {
            var path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void WriteBytes(string relative, byte[] content)
        {
            var path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
