using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Permissions;
using AgentPulse.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Infrastructure.Tests.Permissions;

public sealed class PermissionAgentLoopIntegrationTests
{
    [Theory]
    [InlineData("read", "{\"path\":\"sample.txt\"}")]
    [InlineData("glob", "{\"pattern\":\"**/*.txt\"}")]
    [InlineData("glob", "{\"pattern\":\"/**/*.txt\"}")]
    [InlineData("grep", "{\"pattern\":\"hello\"}")]
    public async Task Default_builtin_tools_still_work_without_prompt(
        string toolName,
        string arguments)
    {
        using var workspace = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "sample.txt"), "hello world");
        var prompt = new QueuePrompt(true);
        var observer = new RecordingObserver();
        var tools = CreateBuiltInTools();
        var loop = CreateLoop(
            new ScriptedClient(ToolResponse("call-1", toolName, arguments), FinalResponse("done")),
            tools,
            prompt,
            new PermissionOptions(),
            workspace.Path);

        var result = await loop.ExecuteAsync(
            CreateRequest(workspace.Path),
            observer);

        Assert.Equal("done", result.Text);
        Assert.True(Assert.Single(observer.Executions).Result.Succeeded);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task Workspace_validation_rejects_external_path_before_prompt()
    {
        using var workspace = new TemporaryDirectory();
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("call-1", "read", "{\"path\":\"../outside.txt\"}"),
                FinalResponse("done")),
            CreateBuiltInTools(),
            prompt,
            new PermissionOptions
            {
                Rules =
                [
                    new PermissionRuleOptions
                    {
                        Tool = "read",
                        Operation = "read",
                        Target = "*",
                        Decision = "Ask",
                    },
                ],
            },
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal(0, prompt.CallCount);
        Assert.False(Assert.Single(observer.Executions).Result.Succeeded);
        Assert.Contains(
            "outside the active workspace",
            observer.Executions[0].Result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_allow_once_executes_and_persists_a_successful_tool_result_in_the_turn()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new CountingPermissionTool();
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{}"),
            FinalResponse("complete"));
        var loop = CreateLoop(client, [tool], prompt, AskOptions(), workspace.Path);

        var result = await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal("complete", result.Text);
        Assert.Equal(1, tool.ExecutionCount);
        var execution = Assert.Single(observer.Executions);
        Assert.True(execution.Result.Succeeded);
        Assert.Equal("call-1", execution.Call.Id);
        Assert.Contains(client.Requests[1].Messages, message =>
            message.Role == ChatModelRole.Tool && message.ToolCallId == "call-1");
    }

    [Fact]
    public async Task Ask_deny_does_not_execute_tool_and_loop_continues()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new CountingPermissionTool();
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("call-denied", "read", "{}"),
                FinalResponse("recovered")),
            [tool],
            new QueuePrompt(true, PermissionApprovalChoice.Deny),
            AskOptions(),
            workspace.Path);

        var result = await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal("recovered", result.Text);
        Assert.Equal(0, tool.ExecutionCount);
        var execution = Assert.Single(observer.Executions);
        Assert.False(execution.Result.Succeeded);
        Assert.Contains("Permission denied", execution.Result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_deny_never_executes_the_tool_and_persists_a_failed_result()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new CountingPermissionTool();
        var observer = new RecordingObserver();
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce);
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("call-explicit-deny", "read", "{}"),
                FinalResponse("handled")),
            [tool],
            prompt,
            new PermissionOptions
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
            },
            workspace.Path);

        var result = await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal("handled", result.Text);
        Assert.Equal(0, prompt.CallCount);
        Assert.Equal(0, tool.ExecutionCount);
        var execution = Assert.Single(observer.Executions);
        Assert.Equal("call-explicit-deny", execution.Call.Id);
        Assert.False(execution.Result.Succeeded);
        Assert.Contains("Permission denied", execution.Result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_interactive_ask_does_not_wait_or_execute()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new CountingPermissionTool();
        var observer = new RecordingObserver();
        var prompt = new QueuePrompt(false);
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("call-denied", "read", "{}"),
                FinalResponse("done")),
            [tool],
            prompt,
            AskOptions(),
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal(0, prompt.CallCount);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(
            "Permission approval is required, but the current run is non-interactive.",
            Assert.Single(observer.Executions).Result.Error);
    }

    [Theory]
    [InlineData("glob", "{\"pattern\":\"**/*\"}", "No files found.")]
    [InlineData("grep", "{\"pattern\":\"shared-needle\"}", "No matches found.")]
    public async Task Resource_non_interactive_ask_returns_failed_tool_result_to_the_model(
        string toolName,
        string arguments,
        string misleadingEmptyResult)
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "a-allowed"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "b-restricted"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "c-after"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "a-allowed", "first.txt"),
            "shared-needle first");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "b-restricted", "private.txt"),
            "shared-needle private-secret");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "c-after", "last.txt"),
            "shared-needle last");
        var prompt = new QueuePrompt(false);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("resource-ask", toolName, arguments),
            FinalResponse("handled"));
        var loop = CreateLoop(
            client,
            CreateBuiltInTools(),
            prompt,
            SearchOptions(toolName, "b-restricted/**", "Ask"),
            workspace.Path);

        var result = await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal("handled", result.Text);
        Assert.Equal(0, prompt.CallCount);
        var execution = Assert.Single(observer.Executions);
        Assert.Equal("resource-ask", execution.Call.Id);
        Assert.Equal(toolName, execution.Call.Name);
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(
            "Permission approval is required, but the current run is non-interactive.",
            execution.Result.Error);
        Assert.Empty(execution.Result.Output);
        Assert.NotEqual(misleadingEmptyResult, execution.Result.Output);
        var toolMessage = Assert.Single(
            client.Requests[1].Messages,
            static message => message.Role == ChatModelRole.Tool);
        Assert.Equal("resource-ask", toolMessage.ToolCallId);
        var toolContent = Assert.IsType<string>(toolMessage.Content);
        Assert.Contains("\"success\":false", toolContent, StringComparison.Ordinal);
        Assert.Contains(
            "Permission approval is required, but the current run is non-interactive.",
            toolContent,
            StringComparison.Ordinal);
        Assert.DoesNotContain(misleadingEmptyResult, toolContent, StringComparison.Ordinal);
        Assert.DoesNotContain("b-restricted/private.txt", toolContent, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", toolContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glob", "{\"pattern\":\"**/*\"}")]
    [InlineData("grep", "{\"pattern\":\"shared-needle\"}")]
    public async Task Resource_permission_infrastructure_failure_aborts_and_reaches_the_model(
        string toolName,
        string arguments)
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "a-allowed"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "b-failure"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "c-after"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "a-allowed", "first.txt"),
            "shared-needle first");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "b-failure", "private.txt"),
            "shared-needle private");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "c-after", "last.txt"),
            "shared-needle last");
        const string error = "Permission infrastructure failed while loading persisted approvals.";
        var gate = new FailingResourcePermissionGate("b-failure/private.txt", error);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("resource-failure", toolName, arguments),
            FinalResponse("recovered"));
        var loop = CreateLoop(client, CreateBuiltInTools(), gate);

        var result = await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal("recovered", result.Text);
        var execution = Assert.Single(observer.Executions);
        Assert.Equal("resource-failure", execution.Call.Id);
        Assert.Equal(toolName, execution.Call.Name);
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(error, execution.Result.Error);
        Assert.Empty(execution.Result.Output);
        Assert.Equal(
            ["a-allowed/first.txt", "b-failure/private.txt"],
            gate.ResourceTargets);
        var toolMessage = Assert.Single(
            client.Requests[1].Messages,
            static message => message.Role == ChatModelRole.Tool);
        var toolContent = Assert.IsType<string>(toolMessage.Content);
        Assert.Contains("\"success\":false", toolContent, StringComparison.Ordinal);
        Assert.Contains(error, toolContent, StringComparison.Ordinal);
        Assert.DoesNotContain("a-allowed/first.txt", toolContent, StringComparison.Ordinal);
        Assert.DoesNotContain("c-after/last.txt", toolContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glob", "{\"pattern\":\"**/*\"}")]
    [InlineData("grep", "{\"pattern\":\"shared-needle\"}")]
    public async Task Cancellation_while_waiting_for_resource_approval_propagates(
        string toolName,
        string arguments)
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "restricted"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "restricted", "private.txt"),
            "shared-needle private-secret");
        var observer = new RecordingObserver();
        var prompt = new WaitingPrompt();
        using var cancellation = new CancellationTokenSource();
        var loop = CreateLoop(
            new ScriptedClient(ToolResponse("resource-cancel", toolName, arguments)),
            CreateBuiltInTools(),
            prompt,
            SearchOptions(toolName, "restricted/**", "Ask"),
            workspace.Path);
        var execution = loop.ExecuteAsync(
            CreateRequest(workspace.Path),
            observer,
            cancellation.Token);

        await prompt.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(1, observer.AssistantToolCallCount);
        Assert.Equal(1, prompt.CallCount);
        Assert.Empty(observer.Executions);
    }

    [Fact]
    public async Task Real_grep_excludes_denied_file_content_from_tool_result_and_model_history()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "a-allowed"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "b-denied"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "c-allowed"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a-allowed", "first.txt"), "shared-needle first");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "b-denied", "private.txt"), "shared-needle private-secret");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "c-allowed", "last.txt"), "shared-needle last");
        var prompt = new QueuePrompt(true);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("grep-call", "grep", "{\"pattern\":\"shared-needle\"}"),
            FinalResponse("done"));
        var loop = CreateLoop(
            client,
            CreateBuiltInTools(),
            prompt,
            SearchOptions("grep", "b-denied/**", "Deny"),
            workspace.Path);

        var result = await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal("done", result.Text);
        var execution = Assert.Single(observer.Executions);
        Assert.True(execution.Result.Succeeded);
        Assert.Contains("a-allowed/first.txt:1", execution.Result.Output, StringComparison.Ordinal);
        Assert.Contains("c-allowed/last.txt:1", execution.Result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", execution.Result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("b-denied/private.txt", execution.Result.Output, StringComparison.Ordinal);
        var toolMessage = Assert.Single(
            client.Requests[1].Messages,
            static message => message.Role == ChatModelRole.Tool);
        var toolContent = Assert.IsType<string>(toolMessage.Content);
        Assert.DoesNotContain("private-secret", toolContent, StringComparison.Ordinal);
        Assert.DoesNotContain("b-denied/private.txt", toolContent, StringComparison.Ordinal);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task Real_glob_excludes_denied_paths_from_tool_result_and_model_history()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "a-allowed"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "b-denied"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "c-allowed"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "a-allowed", "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "b-denied", "private.txt"), "private");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "c-allowed", "last.txt"), "last");
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("glob-call", "glob", "{\"pattern\":\"**/*\"}"),
            FinalResponse("done"));
        var loop = CreateLoop(
            client,
            CreateBuiltInTools(),
            new QueuePrompt(true),
            SearchOptions("glob", "b-denied/**", "Deny"),
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        var execution = Assert.Single(observer.Executions);
        Assert.True(execution.Result.Succeeded);
        Assert.Contains("a-allowed/first.txt", execution.Result.Output, StringComparison.Ordinal);
        Assert.Contains("c-allowed/last.txt", execution.Result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("b-denied/private.txt", execution.Result.Output, StringComparison.Ordinal);
        var toolMessage = Assert.Single(
            client.Requests[1].Messages,
            static message => message.Role == ChatModelRole.Tool);
        Assert.DoesNotContain(
            "b-denied/private.txt",
            Assert.IsType<string>(toolMessage.Content),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_resource_paths_apply_allow_ask_and_deny_without_repeated_group_prompts()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "restricted"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "secrets"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "src", "public.txt"), "needle public");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "restricted", "one.txt"), "needle restricted-one");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "restricted", "two.txt"), "needle restricted-two");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "secrets", "private.txt"), "needle private-secret");
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("grep-mixed", "grep", "{\"pattern\":\"needle\"}"),
                FinalResponse("done")),
            CreateBuiltInTools(),
            prompt,
            new PermissionOptions
            {
                Rules =
                [
                    new PermissionRuleOptions
                    {
                        Tool = "grep",
                        Operation = "search",
                        Target = "restricted/**",
                        Decision = "Ask",
                        Scope = "Project",
                    },
                    new PermissionRuleOptions
                    {
                        Tool = "grep",
                        Operation = "search",
                        Target = "secrets/**",
                        Decision = "Deny",
                    },
                ],
            },
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        var output = Assert.Single(observer.Executions).Result.Output;
        Assert.Contains("src/public.txt", output, StringComparison.Ordinal);
        Assert.Contains("restricted/one.txt", output, StringComparison.Ordinal);
        Assert.Contains("restricted/two.txt", output, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", output, StringComparison.Ordinal);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal("restricted/**", Assert.Single(prompt.Requests).Target);
    }

    [Fact]
    public async Task Global_ask_approval_for_one_candidate_does_not_authorize_other_candidates()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "src", "a.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "src", "b.txt"), "second");
        var prompt = new QueuePrompt(
            true,
            PermissionApprovalChoice.AllowOnce,
            PermissionApprovalChoice.AllowOnce,
            PermissionApprovalChoice.Deny);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse(
                    "glob-global-ask",
                    "glob",
                    "{\"pattern\":\"**/*\",\"basePath\":\"src\"}"),
                FinalResponse("done")),
            CreateBuiltInTools(),
            prompt,
            SearchOptions("glob", "*", "Ask", "Once"),
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        var execution = Assert.Single(observer.Executions);
        Assert.True(execution.Result.Succeeded, execution.Result.Error);
        var output = execution.Result.Output;
        Assert.Contains("src/a.txt", output, StringComparison.Ordinal);
        Assert.DoesNotContain("src/b.txt", output, StringComparison.Ordinal);
        Assert.Equal(3, prompt.CallCount);
        Assert.Equal(
            ["src/**/*", "src/a.txt", "src/b.txt"],
            prompt.Requests.Select(static request => request.Target));
    }

    [Fact]
    public async Task Resource_allow_once_requires_approval_again_for_the_next_tool_request()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "restricted"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "restricted", "file.txt"), "needle");
        var prompt = new QueuePrompt(
            true,
            PermissionApprovalChoice.AllowOnce,
            PermissionApprovalChoice.Deny);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("grep-1", "grep", "{\"pattern\":\"needle\"}"),
                ToolResponse("grep-2", "grep", "{\"pattern\":\"needle\"}"),
                FinalResponse("done")),
            CreateBuiltInTools(),
            prompt,
            SearchOptions("grep", "restricted/**", "Ask", "Project"),
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal(2, prompt.CallCount);
        Assert.Contains("restricted/file.txt", observer.Executions[0].Result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("restricted/file.txt", observer.Executions[1].Result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resource_session_approval_is_reused_by_the_next_request_in_the_same_session()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "restricted"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "restricted", "file.txt"), "needle");
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowSession);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("grep-1", "grep", "{\"pattern\":\"needle\"}"),
                ToolResponse("grep-2", "grep", "{\"pattern\":\"needle\"}"),
                FinalResponse("done")),
            CreateBuiltInTools(),
            prompt,
            SearchOptions("grep", "restricted/**", "Ask", "Session"),
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal(1, prompt.CallCount);
        Assert.Equal(2, observer.Executions.Count);
        Assert.All(observer.Executions, execution =>
            Assert.Contains("restricted/file.txt", execution.Result.Output, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resource_project_approval_reloads_and_new_explicit_deny_overrides_it()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "restricted"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "restricted", "file.txt"), "needle");
        var projectId = ProjectId.New();
        var firstPrompt = new QueuePrompt(true, PermissionApprovalChoice.AllowProject);
        var firstObserver = new RecordingObserver();
        var firstLoop = CreateLoop(
            new ScriptedClient(
                ToolResponse("grep-1", "grep", "{\"pattern\":\"needle\"}"),
                FinalResponse("first")),
            CreateBuiltInTools(),
            firstPrompt,
            SearchOptions("grep", "restricted/**", "Ask", "Project"),
            workspace.Path);

        await firstLoop.ExecuteAsync(
            CreateRequest(workspace.Path, SessionId.New(), projectId),
            firstObserver);

        var reloadPrompt = new QueuePrompt(true);
        var reloadObserver = new RecordingObserver();
        var reloadLoop = CreateLoop(
            new ScriptedClient(
                ToolResponse("grep-2", "grep", "{\"pattern\":\"needle\"}"),
                FinalResponse("second")),
            CreateBuiltInTools(),
            reloadPrompt,
            SearchOptions("grep", "restricted/**", "Ask", "Project"),
            workspace.Path);

        await reloadLoop.ExecuteAsync(
            CreateRequest(workspace.Path, SessionId.New(), projectId),
            reloadObserver);

        Assert.Equal(1, firstPrompt.CallCount);
        Assert.Equal(0, reloadPrompt.CallCount);
        Assert.Contains(
            "restricted/file.txt",
            Assert.Single(reloadObserver.Executions).Result.Output,
            StringComparison.Ordinal);

        var denyObserver = new RecordingObserver();
        var denyLoop = CreateLoop(
            new ScriptedClient(
                ToolResponse("grep-3", "grep", "{\"pattern\":\"needle\"}"),
                FinalResponse("third")),
            CreateBuiltInTools(),
            new QueuePrompt(true),
            SearchOptions("grep", "restricted/**", "Deny"),
            workspace.Path);

        await denyLoop.ExecuteAsync(
            CreateRequest(workspace.Path, SessionId.New(), projectId),
            denyObserver);

        Assert.DoesNotContain(
            "restricted/file.txt",
            Assert.Single(denyObserver.Executions).Result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unclassified_tool_is_denied_before_execution_and_denial_reaches_the_model()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new UnclassifiedCountingTool();
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("unclassified-call", "unclassified", "{}"),
            FinalResponse("handled"));
        var loop = CreateLoop(
            client,
            [tool],
            new QueuePrompt(true),
            new PermissionOptions(),
            workspace.Path);

        var result = await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal("handled", result.Text);
        Assert.Equal(0, tool.ExecutionCount);
        var execution = Assert.Single(observer.Executions);
        Assert.False(execution.Result.Succeeded);
        Assert.Equal("unclassified-call", execution.Call.Id);
        Assert.Equal(
            "Permission metadata is not defined for tool 'unclassified'. Execution was denied.",
            execution.Result.Error);
        var toolMessage = Assert.Single(
            client.Requests[1].Messages,
            static message => message.Role == ChatModelRole.Tool);
        Assert.Contains(
            "Permission metadata is not defined",
            Assert.IsType<string>(toolMessage.Content),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_scope_rejects_fake_project_approval_before_tool_execution()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new CountingPermissionTool();
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("scope-call", "read", "{}"),
                FinalResponse("handled")),
            [tool],
            new QueuePrompt(true, PermissionApprovalChoice.AllowProject),
            AskOptions(PermissionScope.Once),
            workspace.Path);

        await loop.ExecuteAsync(CreateRequest(workspace.Path), observer);

        Assert.Equal(0, tool.ExecutionCount);
        var execution = Assert.Single(observer.Executions);
        Assert.False(execution.Result.Succeeded);
        Assert.Contains("exceeds the configured Once scope", execution.Result.Error, StringComparison.Ordinal);
        var permissionDirectory = Path.Combine(workspace.Path, ".permissions");
        Assert.Empty(Directory.EnumerateFiles(permissionDirectory, "*.permissions.json"));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_approval_prevents_tool_execution()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new CountingPermissionTool();
        var observer = new RecordingObserver();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var loop = CreateLoop(
            new ScriptedClient(ToolResponse("call-1", "read", "{}")),
            [tool],
            new WaitingPrompt(),
            AskOptions(),
            workspace.Path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.ExecuteAsync(
            CreateRequest(workspace.Path),
            observer,
            cancellation.Token));

        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(1, observer.AssistantToolCallCount);
        Assert.Empty(observer.Executions);
    }

    private static IReadOnlyList<IAgentTool> CreateBuiltInTools()
    {
        var options = new AgentToolOptions();
        var resolver = new WorkspacePathResolver();
        return
        [
            new ReadAgentTool(resolver, options),
            new GlobAgentTool(resolver, options),
            new GrepAgentTool(resolver, options),
        ];
    }

    private static AgentPulse.Application.AgentLoop.AgentLoop CreateLoop(
        IChatModelClient client,
        IReadOnlyList<IAgentTool> tools,
        IPermissionApprovalPrompt prompt,
        PermissionOptions options,
        string permissionRoot)
    {
        var gate = new PermissionGate(
            new PermissionRuleEvaluator(),
            new InMemorySessionPermissionStore(),
            new JsonProjectPermissionStore(Path.Combine(permissionRoot, ".permissions")),
            prompt,
            options,
            NullLogger<PermissionGate>.Instance);
        return CreateLoop(client, tools, gate);
    }

    private static AgentPulse.Application.AgentLoop.AgentLoop CreateLoop(
        IChatModelClient client,
        IReadOnlyList<IAgentTool> tools,
        IPermissionGate gate)
    {
        return new AgentPulse.Application.AgentLoop.AgentLoop(
            client,
            new AgentToolRegistry(tools),
            new AgentToolOptions { ToolTimeout = TimeSpan.FromSeconds(5) },
            NullLogger<AgentPulse.Application.AgentLoop.AgentLoop>.Instance,
            gate);
    }

    private static AgentLoopRequest CreateRequest(
        string workspaceRoot,
        SessionId? sessionId = null,
        ProjectId? projectId = null)
    {
        return new AgentLoopRequest(
            [
                new ChatModelMessage(ChatModelRole.System, "system"),
                new ChatModelMessage(ChatModelRole.User, "prompt"),
            ],
            workspaceRoot,
            SessionId: sessionId ?? SessionId.New(),
            ProjectId: projectId ?? ProjectId.New());
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

    private static PermissionOptions SearchOptions(
        string tool,
        string target,
        string decision,
        string scope = "Project") => new()
    {
        Rules =
        [
            new PermissionRuleOptions
            {
                Tool = tool,
                Operation = "search",
                Target = target,
                Decision = decision,
                Scope = scope,
            },
        ],
    };

    private static ChatModelResponse ToolResponse(string id, string name, string arguments) =>
        new(null, [new ChatModelToolCall(id, name, arguments, 1)], ModelFinishReason.ToolCalls);

    private static ChatModelResponse FinalResponse(string text) =>
        new(text, [], ModelFinishReason.Stop);

    private sealed class CountingPermissionTool : IAgentTool, IPermissionAwareAgentTool
    {
        public string Name => "read";

        public string Description => "test";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public int ExecutionCount { get; private set; }

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(AgentToolResult.Success("file content"));
        }

        public PermissionTargetResolution ResolvePermissionTarget(
            JsonElement arguments,
            AgentToolExecutionContext context) =>
            PermissionTargetResolution.Success("read", "src/Program.cs");
    }

    private sealed class UnclassifiedCountingTool : IAgentTool
    {
        public string Name => "unclassified";

        public string Description => "test";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public int ExecutionCount { get; private set; }

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(AgentToolResult.Success("unexpected"));
        }
    }

    private sealed class FailingResourcePermissionGate(
        string failureTarget,
        string error) : IPermissionGate
    {
        public List<string> ResourceTargets { get; } = [];

        public Task<PermissionAuthorizationResult> AuthorizeAsync(
            IAgentTool tool,
            JsonElement arguments,
            AgentToolExecutionContext toolContext,
            SessionId? sessionId,
            ProjectId? projectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PermissionAuthorizationResult.Allow());
        }

        public Task<PermissionAuthorizationResult> AuthorizeAsync(
            IAgentTool tool,
            JsonElement arguments,
            AgentToolExecutionContext toolContext,
            SessionId? sessionId,
            ProjectId? projectId,
            PermissionAuthorizationContext authorizationContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PermissionAuthorizationResult.Allow());
        }

        public Task<PermissionAuthorizationResult> AuthorizeResourceAsync(
            IAgentTool tool,
            string operation,
            string target,
            string? description,
            AgentToolExecutionContext toolContext,
            SessionId? sessionId,
            ProjectId? projectId,
            PermissionAuthorizationContext authorizationContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceTargets.Add(target);
            return Task.FromResult(string.Equals(target, failureTarget, StringComparison.Ordinal)
                ? PermissionAuthorizationResult.Reject(AgentToolResult.Failure(error))
                : PermissionAuthorizationResult.Allow());
        }
    }

    private sealed class ScriptedClient(params ChatModelResponse[] responses) : IChatModelClient
    {
        private readonly Queue<ChatModelResponse> _responses = new(responses);

        public List<ChatModelRequest> Requests { get; } = [];

        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ChatModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class QueuePrompt(bool interactive, params PermissionApprovalChoice[] choices)
        : IPermissionApprovalPrompt
    {
        private readonly Queue<PermissionApprovalChoice> _choices = new(choices);

        public bool IsInteractive { get; } = interactive;

        public int CallCount { get; private set; }

        public List<PermissionRequest> Requests { get; } = [];

        public Task<PermissionApprovalChoice> RequestApprovalAsync(
            PermissionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Requests.Add(request);
            return Task.FromResult(_choices.Dequeue());
        }
    }

    private sealed class WaitingPrompt : IPermissionApprovalPrompt
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsInteractive => true;

        public int CallCount { get; private set; }

        public Task Entered => _entered.Task;

        public async Task<PermissionApprovalChoice> RequestApprovalAsync(
            PermissionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return PermissionApprovalChoice.Deny;
        }
    }

    private sealed class RecordingObserver : IAgentLoopObserver
    {
        public List<AgentLoopToolExecution> Executions { get; } = [];

        public int AssistantToolCallCount { get; private set; }

        public Task RecordAssistantResponseAsync(
            ChatModelResponse response,
            int iteration,
            CancellationToken cancellationToken)
        {
            if (response.ToolCalls.Count > 0)
            {
                AssistantToolCallCount++;
            }

            return Task.CompletedTask;
        }

        public Task RecordToolResultAsync(
            AgentLoopToolExecution result,
            int iteration,
            CancellationToken cancellationToken)
        {
            Executions.Add(result);
            return Task.CompletedTask;
        }

        public Task CompleteToolTurnAsync(
            int iteration,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agentpulse-permission-integration",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
