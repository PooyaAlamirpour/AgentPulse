using System.Runtime.CompilerServices;
using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Mutations;
using AgentPulse.Infrastructure.Permissions;
using AgentPulse.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class MutationAgentLoopIntegrationTests
{
    [Fact]
    public async Task Fake_model_write_flow_prompts_with_diff_creates_file_and_persists_result()
    {
        using var workspace = new TemporaryWorkspace();
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("write-1", "write", "{\"path\":\"new.txt\",\"content\":\"created\\n\"}"),
            FinalResponse("done"));
        var loop = CreateLoop(
            client,
            [new WriteAgentTool(CreateService())],
            prompt,
            workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("done", response.Text);
        Assert.Equal("created\n", await File.ReadAllTextAsync(workspace.PathOf("new.txt")));
        Assert.Contains("+++ b/new.txt", Assert.Single(prompt.Requests).Description!, StringComparison.Ordinal);
        var execution = Assert.Single(observer.Executions);
        Assert.True(execution.Result.Succeeded);
        Assert.Equal("false", execution.Result.Metadata["diffTruncated"]);
        Assert.True(int.Parse(execution.Result.Metadata["diffCharacterCount"], System.Globalization.CultureInfo.InvariantCulture) > 0);
        var toolMessage = Assert.Single(
            client.Requests[1].Messages,
            static message => message.Role == ChatModelRole.Tool);
        using var toolResult = System.Text.Json.JsonDocument.Parse(toolMessage.Content!);
        Assert.Contains(
            "+++ b/new.txt",
            toolResult.RootElement.GetProperty("output").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_model_existing_file_write_without_hash_fails_before_permission_and_returns_error_to_model()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(workspace.PathOf("notes"));
        var path = workspace.PathOf("notes/existing.txt");
        await File.WriteAllTextAsync(path, "ORIGINAL_CONTENT");
        var before = await File.ReadAllBytesAsync(path);
        var prompt = new QueuePrompt(true);
        var observer = new RecordingObserver();
        const string arguments =
            "{\"path\":\"notes/existing.txt\",\"content\":\"OVERWRITTEN_CONTENT\"}";
        var client = new ScriptedClient(
            ToolResponse("write-existing-missing-hash", "write", arguments),
            FinalResponse("handled"));
        var loop = CreateLoop(
            client,
            [new WriteAgentTool(CreateService())],
            prompt,
            workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("handled", response.Text);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, prompt.CallCount);
        var execution = Assert.Single(observer.Executions);
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(
            AgentToolFailureClassification.Deterministic,
            execution.Result.FailureClassification);
        Assert.Contains("expected SHA-256", execution.Result.Error, StringComparison.Ordinal);

        var nextRequest = client.Requests[1];
        var assistantToolCall = Assert.Single(
            nextRequest.Messages,
            static message => message.Role == ChatModelRole.Assistant && message.ToolCalls.Count > 0);
        var persistedCall = Assert.Single(assistantToolCall.ToolCalls);
        Assert.Equal("write-existing-missing-hash", persistedCall.Id);
        Assert.Equal("write", persistedCall.Name);
        Assert.Equal(arguments, persistedCall.ArgumentsJson);

        var toolMessage = Assert.Single(
            nextRequest.Messages,
            static message => message.Role == ChatModelRole.Tool);
        Assert.Equal("write-existing-missing-hash", toolMessage.ToolCallId);
        Assert.Equal("write", toolMessage.ToolName);
        using var toolResult = System.Text.Json.JsonDocument.Parse(toolMessage.Content!);
        Assert.False(toolResult.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(
            "expected SHA-256",
            toolResult.RootElement.GetProperty("error").GetString()!,
            StringComparison.Ordinal);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Fake_model_existing_file_write_with_correct_hash_prompts_with_diff_and_overwrites()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(workspace.PathOf("notes"));
        var path = workspace.PathOf("notes/existing.txt");
        await File.WriteAllTextAsync(path, "ORIGINAL_CONTENT");
        var hash = TextFileCodec.ComputeSha256(await File.ReadAllBytesAsync(path));
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse(
                "write-existing-correct-hash",
                "write",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    path = "notes/existing.txt",
                    content = "OVERWRITTEN_CONTENT",
                    expected_sha256 = hash,
                })),
            FinalResponse("done"));
        var loop = CreateLoop(
            client,
            [new WriteAgentTool(CreateService())],
            prompt,
            workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("done", response.Text);
        Assert.Equal("OVERWRITTEN_CONTENT", await File.ReadAllTextAsync(path));
        Assert.Equal(1, prompt.CallCount);
        Assert.Contains(
            "--- a/notes/existing.txt",
            Assert.Single(prompt.Requests).Description!,
            StringComparison.Ordinal);
        var result = Assert.Single(observer.Executions).Result;
        Assert.True(result.Succeeded);
        Assert.Equal(
            TextFileCodec.ComputeSha256(await File.ReadAllBytesAsync(path)),
            result.Metadata["sha256After"]);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Fake_model_read_then_edit_flow_changes_file_after_approval()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("file.txt"), "before\n");
        var options = new AgentToolOptions();
        var resolver = new WorkspacePathResolver();
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.AllowOnce);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("read-1", "read", "{\"path\":\"file.txt\"}"),
            ToolResponse("edit-1", "edit", "{\"path\":\"file.txt\",\"old_text\":\"before\",\"new_text\":\"after\"}"),
            FinalResponse("edited"));
        var loop = CreateLoop(
            client,
            [new ReadAgentTool(resolver, options), new EditAgentTool(CreateService())],
            prompt,
            workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("edited", response.Text);
        Assert.Equal("after\n", await File.ReadAllTextAsync(workspace.PathOf("file.txt")));
        Assert.Equal(2, observer.Executions.Count);
        Assert.True(observer.Executions[0].Result.Succeeded);
        Assert.True(observer.Executions[1].Result.Succeeded);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public async Task Permission_deny_persists_failed_result_keeps_file_absent_and_loop_continues()
    {
        using var workspace = new TemporaryWorkspace();
        var prompt = new QueuePrompt(true, PermissionApprovalChoice.Deny);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("write-denied", "write", "{\"path\":\"denied.txt\",\"content\":\"x\"}"),
                FinalResponse("recovered")),
            [new WriteAgentTool(CreateService())],
            prompt,
            workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("recovered", response.Text);
        Assert.False(File.Exists(workspace.PathOf("denied.txt")));
        var result = Assert.Single(observer.Executions).Result;
        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
    }

    [Fact]
    public async Task Non_interactive_default_ask_fails_without_hanging_or_creating_file()
    {
        using var workspace = new TemporaryWorkspace();
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse("write-noninteractive", "write", "{\"path\":\"new.txt\",\"content\":\"x\"}"),
                FinalResponse("handled")),
            [new WriteAgentTool(CreateService())],
            new QueuePrompt(false),
            workspace.Root);

        await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.False(File.Exists(workspace.PathOf("new.txt")));
        Assert.Equal(
            "Permission approval is required, but the current run is non-interactive.",
            Assert.Single(observer.Executions).Result.Error);
    }

    [Fact]
    public async Task Non_interactive_ask_after_correct_hash_keeps_existing_file_unchanged()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("existing.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await File.ReadAllBytesAsync(path);
        var hash = TextFileCodec.ComputeSha256(before);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse(
                    "write-existing-noninteractive",
                    "write",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        path = "existing.txt",
                        content = "after",
                        expected_sha256 = hash,
                    })),
                FinalResponse("handled")),
            [new WriteAgentTool(CreateService())],
            new QueuePrompt(false),
            workspace.Root);

        await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal(
            "Permission approval is required, but the current run is non-interactive.",
            Assert.Single(observer.Executions).Result.Error);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Multi_edit_staged_failure_persists_deterministic_result_without_partial_write()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "one two");
        var before = await File.ReadAllBytesAsync(path);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse(
                    "multi-failed",
                    "multi_edit",
                    """
                    {"path":"file.txt","edits":[
                      {"old_text":"one","new_text":"first"},
                      {"old_text":"missing","new_text":"second"}
                    ]}
                    """),
                FinalResponse("handled")),
            [new MultiEditAgentTool(CreateService())],
            new QueuePrompt(true),
            workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("handled", response.Text);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var result = Assert.Single(observer.Executions).Result;
        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Contains("operation 2", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multi_file_patch_commit_failure_is_rolled_back_and_persisted()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.PathOf("a.txt");
        var second = workspace.PathOf("b.txt");
        await File.WriteAllTextAsync(first, "a\n");
        await File.WriteAllTextAsync(second, "b\n");
        var firstBefore = await File.ReadAllBytesAsync(first);
        var secondBefore = await File.ReadAllBytesAsync(second);
        var observer = new RecordingObserver();
        var options = CreateOptions();
        var tool = new ApplyPatchAgentTool(
            new ApplyPatchParser(options),
            CreateService(options, new FaultingMutationFileSystem(2)));
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse(
                    "patch-failed",
                    "apply_patch",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        patch_text = "*** Begin Patch\n*** Update File: a.txt\n@@\n-a\n+changed-a\n*** Update File: b.txt\n@@\n-b\n+changed-b\n*** End Patch",
                    })),
                FinalResponse("recovered")),
            [tool],
            new QueuePrompt(
                true,
                PermissionApprovalChoice.AllowOnce,
                PermissionApprovalChoice.AllowOnce),
            workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("recovered", response.Text);
        Assert.Equal(firstBefore, await File.ReadAllBytesAsync(first));
        Assert.Equal(secondBefore, await File.ReadAllBytesAsync(second));
        var result = Assert.Single(observer.Executions).Result;
        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Transient, result.FailureClassification);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Non_interactive_patch_ask_aborts_the_entire_patch()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("existing.txt"), "before\n");
        var before = await File.ReadAllBytesAsync(workspace.PathOf("existing.txt"));
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse(
                    "patch-noninteractive",
                    "apply_patch",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        patch_text = "*** Begin Patch\n*** Add File: created.txt\n+created\n*** Update File: existing.txt\n@@\n-before\n+after\n*** End Patch",
                    })),
                FinalResponse("handled")),
            [CreatePatchTool()],
            new QueuePrompt(false),
            workspace.Root);

        await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.False(File.Exists(workspace.PathOf("created.txt")));
        Assert.Equal(before, await File.ReadAllBytesAsync(workspace.PathOf("existing.txt")));
        Assert.Equal(
            "Permission approval is required, but the current run is non-interactive.",
            Assert.Single(observer.Executions).Result.Error);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_mutation_approval_leaves_workspace_clean_and_loop_reusable()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var client = new ScriptedClient(
            ToolResponse("write-cancelled", "write", "{\"path\":\"cancelled.txt\",\"content\":\"x\"}"),
            FinalResponse("continued"));
        var loop = CreateLoop(
            client,
            [new WriteAgentTool(CreateService())],
            new CancelingPrompt(cancellation),
            workspace.Root);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loop.ExecuteAsync(CreateRequest(workspace.Root), cancellationToken: cancellation.Token));

        Assert.False(File.Exists(workspace.PathOf("cancelled.txt")));
        AssertNoMutationArtifacts(workspace.Root);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root));
        Assert.Equal("continued", response.Text);
    }

    [Fact]
    public async Task Large_write_diff_is_bounded_for_model_delivery_without_truncating_the_file()
    {
        using var workspace = new TemporaryWorkspace();
        var content = string.Join("\n", Enumerable.Range(1, 400).Select(static value => $"line-{value:D4}")) + "\n";
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse(
                "write-large",
                "write",
                System.Text.Json.JsonSerializer.Serialize(new { path = "large.txt", content })),
            FinalResponse("done"));
        var loop = CreateLoop(
            client,
            [new WriteAgentTool(CreateService())],
            new QueuePrompt(true, PermissionApprovalChoice.AllowOnce),
            workspace.Root,
            maxOutputCharacters: 512);

        var response = await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal("done", response.Text);
        Assert.Equal(content, await File.ReadAllTextAsync(workspace.PathOf("large.txt")));
        var result = Assert.Single(observer.Executions).Result;
        Assert.True(AgentToolResultLimiter.CalculateSize(result) <= 512);
        Assert.Equal("true", result.Metadata["diffTruncated"]);
        Assert.Equal("400", result.Metadata["additions"]);
        Assert.DoesNotContain("diff", result.Metadata.Keys);
    }

    [Fact]
    public async Task Large_edit_diff_is_bounded_after_the_complete_edit_commits()
    {
        using var workspace = new TemporaryWorkspace();
        var before = string.Join("\n", Enumerable.Range(1, 300).Select(static value => $"before-{value:D4}")) + "\n";
        var after = string.Join("\n", Enumerable.Range(1, 300).Select(static value => $"after-{value:D4}")) + "\n";
        await File.WriteAllTextAsync(workspace.PathOf("large.txt"), before);
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse(
                    "edit-large",
                    "edit",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        path = "large.txt",
                        old_text = before,
                        new_text = after,
                    })),
                FinalResponse("done")),
            [new EditAgentTool(CreateService())],
            new QueuePrompt(true, PermissionApprovalChoice.AllowOnce),
            workspace.Root,
            maxOutputCharacters: 512);

        await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal(after, await File.ReadAllTextAsync(workspace.PathOf("large.txt")));
        var result = Assert.Single(observer.Executions).Result;
        Assert.True(AgentToolResultLimiter.CalculateSize(result) <= 512);
        Assert.Equal("true", result.Metadata["diffTruncated"]);
        Assert.Equal("300", result.Metadata["additions"]);
        Assert.Equal("300", result.Metadata["deletions"]);
    }

    [Fact]
    public async Task Large_multi_file_patch_result_is_bounded_without_affecting_transactional_commit()
    {
        using var workspace = new TemporaryWorkspace();
        var firstLines = Enumerable.Range(1, 150).Select(static value => $"+first-{value:D4}");
        var secondLines = Enumerable.Range(1, 150).Select(static value => $"+second-{value:D4}");
        var patch = string.Join("\n", new[]
        {
            "*** Begin Patch",
            "*** Add File: first.txt",
        }.Concat(firstLines).Concat(new[]
        {
            "*** Add File: second.txt",
        }).Concat(secondLines).Concat(new[]
        {
            "*** End Patch",
        }));
        var observer = new RecordingObserver();
        var loop = CreateLoop(
            new ScriptedClient(
                ToolResponse(
                    "patch-large",
                    "apply_patch",
                    System.Text.Json.JsonSerializer.Serialize(new { patch_text = patch })),
                FinalResponse("done")),
            [CreatePatchTool()],
            new QueuePrompt(
                true,
                PermissionApprovalChoice.AllowOnce,
                PermissionApprovalChoice.AllowOnce),
            workspace.Root,
            maxOutputCharacters: 640);

        await loop.ExecuteAsync(CreateRequest(workspace.Root), observer);

        Assert.Equal(150, (await File.ReadAllLinesAsync(workspace.PathOf("first.txt"))).Length);
        Assert.Equal(150, (await File.ReadAllLinesAsync(workspace.PathOf("second.txt"))).Length);
        var result = Assert.Single(observer.Executions).Result;
        Assert.True(AgentToolResultLimiter.CalculateSize(result) <= 640);
        Assert.Equal("true", result.Metadata["diffTruncated"]);
        Assert.Equal("300", result.Metadata["additions"]);
        Assert.Contains("first.txt", result.Metadata["paths"], StringComparison.Ordinal);
        Assert.Contains("second.txt", result.Metadata["paths"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_deterministic_mutation_failure_remains_compatible_with_no_progress_detection()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("existing.txt"), "before");
        var firstCall = ToolResponse(
            "write-failed-1",
            "write",
            "{\"path\":\"existing.txt\",\"content\":\"after\"}");
        var secondCall = ToolResponse(
            "write-failed-2",
            "write",
            "{\"path\":\"existing.txt\",\"content\":\"after\"}");
        var loop = CreateLoop(
            new ScriptedClient(firstCall, secondCall, FinalResponse("unreachable")),
            [new WriteAgentTool(CreateService())],
            new QueuePrompt(true),
            workspace.Root);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() =>
            loop.ExecuteAsync(CreateRequest(workspace.Root)));

        Assert.Equal(AgentLoopErrorCode.NoProgress, exception.Code);
        Assert.Equal("before", await File.ReadAllTextAsync(workspace.PathOf("existing.txt")));
    }

    private static AgentPulse.Application.AgentLoop.AgentLoop CreateLoop(
        IChatModelClient client,
        IReadOnlyList<IAgentTool> tools,
        IPermissionApprovalPrompt prompt,
        string workspaceRoot,
        int maxOutputCharacters = 30_000)
    {
        var gate = new PermissionGate(
            new PermissionRuleEvaluator(),
            new InMemorySessionPermissionStore(),
            new JsonProjectPermissionStore(Path.Combine(workspaceRoot, ".permissions")),
            prompt,
            new PermissionOptions(),
            NullLogger<PermissionGate>.Instance);
        return new AgentPulse.Application.AgentLoop.AgentLoop(
            client,
            new AgentToolRegistry(tools),
            new AgentToolOptions
            {
                ToolTimeout = TimeSpan.FromSeconds(5),
                MaxOutputCharacters = maxOutputCharacters,
            },
            NullLogger<AgentPulse.Application.AgentLoop.AgentLoop>.Instance,
            gate);
    }

    private static AgentLoopRequest CreateRequest(string workspaceRoot) => new(
        [
            new ChatModelMessage(ChatModelRole.System, "system"),
            new ChatModelMessage(ChatModelRole.User, "prompt"),
        ],
        workspaceRoot,
        SessionId: SessionId.New(),
        ProjectId: ProjectId.New());

    private static ChatModelResponse ToolResponse(string id, string name, string arguments) =>
        new(null, [new ChatModelToolCall(id, name, arguments, 1)], ModelFinishReason.ToolCalls);

    private static ChatModelResponse FinalResponse(string text) =>
        new(text, [], ModelFinishReason.Stop);

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

    private sealed class CancelingPrompt(CancellationTokenSource source)
        : IPermissionApprovalPrompt
    {
        public bool IsInteractive => true;

        public Task<PermissionApprovalChoice> RequestApprovalAsync(
            PermissionRequest request,
            CancellationToken cancellationToken)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PermissionApprovalChoice.Deny);
        }
    }

    private sealed class RecordingObserver : IAgentLoopObserver
    {
        public List<AgentLoopToolExecution> Executions { get; } = [];

        public Task RecordAssistantResponseAsync(
            ChatModelResponse response,
            int iteration,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
}
