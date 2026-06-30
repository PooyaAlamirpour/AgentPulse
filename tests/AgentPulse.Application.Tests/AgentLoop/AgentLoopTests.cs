using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Application.Tests.AgentLoop;

public sealed class AgentLoopTests
{
    private static readonly IReadOnlyList<ChatModelMessage> InitialMessages =
    [
        new(ChatModelRole.System, "system"),
        new(ChatModelRole.User, "prompt"),
    ];

    [Fact]
    public async Task Final_text_without_tool_call_completes_immediately()
    {
        var client = new ScriptedClient(new ChatModelResponse(
            "done",
            [],
            ModelFinishReason.Stop));
        var loop = CreateLoop(client, []);

        var result = await loop.ExecuteAsync(new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal("done", result.Text);
        Assert.Equal(1, result.Iterations);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Tool_call_result_is_added_before_next_model_request()
    {
        var client = new ScriptedClient(
            ToolResponse("call-1", "echo", "{\"value\":\"hello\"}"),
            FinalResponse("complete"));
        var tool = new RecordingTool("echo", AgentToolResult.Success("hello"));
        var observer = new RecordingObserver();
        var loop = CreateLoop(client, [tool]);

        var result = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer);

        Assert.Equal("complete", result.Text);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1].Messages, static message =>
            message.Role == ChatModelRole.Tool && message.ToolCallId == "call-1");
        Assert.Single(observer.ToolTurns);
        Assert.True(observer.ToolTurns[0][0].Result.Succeeded);
    }

    [Fact]
    public async Task Multiple_tool_calls_run_in_deterministic_order()
    {
        var response = new ChatModelResponse(
            null,
            [
                new ChatModelToolCall("call-2", "second", "{}", 2),
                new ChatModelToolCall("call-1", "first", "{}", 1),
            ],
            ModelFinishReason.ToolCalls);
        var order = new List<string>();
        var client = new ScriptedClient(response, FinalResponse("done"));
        var loop = CreateLoop(
            client,
            [
                new CallbackTool("first", () => order.Add("first")),
                new CallbackTool("second", () => order.Add("second")),
            ]);

        await loop.ExecuteAsync(new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal(["first", "second"], order);
        var resultMessages = client.Requests[1].Messages.Where(static value => value.Role == ChatModelRole.Tool).ToArray();
        Assert.Equal(["call-1", "call-2"], resultMessages.Select(static value => value.ToolCallId));
    }

    [Fact]
    public async Task Tool_calls_can_continue_across_multiple_iterations()
    {
        var client = new ScriptedClient(
            ToolResponse("call-1", "echo", "{}"),
            ToolResponse("call-2", "echo", "{}"),
            FinalResponse("done"));
        var loop = CreateLoop(client, [new RecordingTool("echo", AgentToolResult.Success("ok"))]);

        var result = await loop.ExecuteAsync(new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal(3, result.Iterations);
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task Unknown_tool_becomes_a_failed_tool_result_and_loop_continues()
    {
        var client = new ScriptedClient(
            ToolResponse("call-1", "missing", "{}"),
            FinalResponse("done"));
        var observer = new RecordingObserver();
        var loop = CreateLoop(client, []);

        await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer);

        var execution = Assert.Single(Assert.Single(observer.ToolTurns));
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(
            AgentToolFailureClassification.Deterministic,
            execution.Result.FailureClassification);
        Assert.Contains("Unknown tool", execution.Result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_json_becomes_a_failed_tool_result()
    {
        var client = new ScriptedClient(
            ToolResponse("call-1", "echo", "{"),
            FinalResponse("done"));
        var observer = new RecordingObserver();
        var loop = CreateLoop(client, [new RecordingTool("echo", AgentToolResult.Success("unused"))]);

        await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer);

        var execution = Assert.Single(Assert.Single(observer.ToolTurns));
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(
            AgentToolFailureClassification.Deterministic,
            execution.Result.FailureClassification);
        Assert.Contains("Invalid JSON", execution.Result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_exception_becomes_a_failed_tool_result()
    {
        var client = new ScriptedClient(
            ToolResponse("call-1", "broken", "{}"),
            FinalResponse("done"));
        var observer = new RecordingObserver();
        var loop = CreateLoop(client, [new ThrowingTool()]);

        await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer);

        var execution = Assert.Single(Assert.Single(observer.ToolTurns));
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(
            AgentToolFailureClassification.Unknown,
            execution.Result.FailureClassification);
        Assert.Contains("failed", execution.Result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var loop = CreateLoop(new ScriptedClient(FinalResponse("unused")), []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_during_tool_execution_is_propagated()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var loop = CreateLoop(
            new ScriptedClient(ToolResponse("call-1", "wait", "{}")),
            [new WaitingTool()]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            cancellationToken: cancellation.Token));
    }


    [Fact]
    public async Task Tool_timeout_becomes_failed_result_and_loop_continues()
    {
        var client = new ScriptedClient(
            ToolResponse("call-timeout", "wait", "{}"),
            FinalResponse("recovered"));
        var observer = new RecordingObserver();
        var loop = new AgentPulse.Application.AgentLoop.AgentLoop(
            client,
            new AgentToolRegistry([new WaitingTool()]),
            new AgentToolOptions
            {
                MaxToolIterations = 2,
                ToolTimeout = TimeSpan.FromMilliseconds(25),
            },
            NullLogger<AgentPulse.Application.AgentLoop.AgentLoop>.Instance);

        var result = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer);

        Assert.Equal("recovered", result.Text);
        var execution = Assert.Single(Assert.Single(observer.ToolTurns));
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(
            AgentToolFailureClassification.Transient,
            execution.Result.FailureClassification);
        Assert.Equal("call-timeout", execution.Call.Id);
        Assert.Contains("timeout", execution.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1].Messages, message =>
            message.Role == ChatModelRole.Tool && message.ToolCallId == "call-timeout");
    }

    [Fact]
    public async Task Repeated_deterministic_failure_stops_before_max_iterations_and_preserves_reason()
    {
        const string reason = "The requested file does not exist.";
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{\"path\":\"missing.txt\"}"),
            ToolResponse("call-2", "read", "{\"path\":\"missing.txt\"}"));
        var tool = new CountingResultTool(
            "read",
            AgentToolResult.Failure(
                reason,
                classification: AgentToolFailureClassification.Deterministic));
        var observer = new RecordingObserver();
        var loop = CreateLoop(client, [tool], maxIterations: 8);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer));

        Assert.Equal(AgentLoopErrorCode.NoProgress, exception.Code);
        Assert.Contains("without making progress", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Tool: read", exception.Message, StringComparison.Ordinal);
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, tool.ExecutionCount);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(2, observer.ToolTurns.Count);
        Assert.Equal("call-2", Assert.Single(observer.ToolTurns[1]).Call.Id);
    }

    [Fact]
    public async Task Different_arguments_do_not_trigger_no_progress_detection()
    {
        var failure = AgentToolResult.Failure(
            "Invalid arguments.",
            classification: AgentToolFailureClassification.Deterministic);
        var tool = new CountingResultTool("read", failure);
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{\"path\":\"first.txt\"}"),
            ToolResponse("call-2", "read", "{\"path\":\"second.txt\"}"),
            FinalResponse("recovered"));
        var loop = CreateLoop(client, [tool]);

        var result = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal("recovered", result.Text);
        Assert.Equal(2, tool.ExecutionCount);
    }

    [Fact]
    public async Task Different_tool_does_not_trigger_no_progress_detection()
    {
        var failure = AgentToolResult.Failure(
            "Rejected.",
            classification: AgentToolFailureClassification.Deterministic);
        var first = new CountingResultTool("read", failure);
        var second = new CountingResultTool("glob", failure);
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{}"),
            ToolResponse("call-2", "glob", "{}"),
            FinalResponse("recovered"));
        var loop = CreateLoop(client, [first, second]);

        var result = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal("recovered", result.Text);
        Assert.Equal(1, first.ExecutionCount);
        Assert.Equal(1, second.ExecutionCount);
    }

    [Fact]
    public async Task Different_tool_call_in_a_batch_resets_the_previous_failure()
    {
        var failure = AgentToolResult.Failure(
            "Rejected.",
            classification: AgentToolFailureClassification.Deterministic);
        var read = new CountingResultTool("read", failure);
        var glob = new CountingResultTool("glob", failure);
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{}"),
            new ChatModelResponse(
                null,
                [
                    new ChatModelToolCall("call-2", "glob", "{}", 1),
                    new ChatModelToolCall("call-3", "read", "{}", 2),
                ],
                ModelFinishReason.ToolCalls),
            FinalResponse("recovered"));
        var loop = CreateLoop(client, [read, glob]);

        var result = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal("recovered", result.Text);
        Assert.Equal(2, read.ExecutionCount);
        Assert.Equal(1, glob.ExecutionCount);
    }

    [Fact]
    public async Task Identical_multi_tool_batch_detects_repeated_deterministic_failures()
    {
        var failure = AgentToolResult.Failure(
            "Rejected.",
            classification: AgentToolFailureClassification.Deterministic);
        var read = new CountingResultTool("read", failure);
        var glob = new CountingResultTool("glob", failure);
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            new ChatModelResponse(
                null,
                [
                    new ChatModelToolCall("read-1", "read", "{\"path\":\"file.txt\"}", 1),
                    new ChatModelToolCall("glob-1", "glob", "{\"pattern\":\"*.cs\"}", 2),
                ],
                ModelFinishReason.ToolCalls),
            new ChatModelResponse(
                null,
                [
                    new ChatModelToolCall("read-2", "read", "{\"path\":\"file.txt\"}", 1),
                    new ChatModelToolCall("glob-2", "glob", "{\"pattern\":\"*.cs\"}", 2),
                ],
                ModelFinishReason.ToolCalls));
        var loop = CreateLoop(client, [read, glob]);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer));

        Assert.Equal(AgentLoopErrorCode.NoProgress, exception.Code);
        Assert.Equal(2, read.ExecutionCount);
        Assert.Equal(2, glob.ExecutionCount);
        Assert.Equal(2, observer.ToolTurns.Count);
        Assert.All(observer.ToolTurns, turn => Assert.Equal(2, turn.Count));
    }

    [Fact]
    public async Task Successful_result_resets_repeated_failure_counter()
    {
        var deterministicFailure = AgentToolResult.Failure(
            "The requested file does not exist.",
            classification: AgentToolFailureClassification.Deterministic);
        var tool = new QueueResultTool(
            "read",
            deterministicFailure,
            AgentToolResult.Success("created"),
            deterministicFailure);
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{\"path\":\"file.txt\"}"),
            ToolResponse("call-2", "read", "{\"path\":\"file.txt\"}"),
            ToolResponse("call-3", "read", "{\"path\":\"file.txt\"}"),
            FinalResponse("done"));
        var loop = CreateLoop(client, [tool]);

        var result = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal("done", result.Text);
        Assert.Equal(3, tool.ExecutionCount);
    }

    [Fact]
    public async Task Repeated_transient_failure_does_not_trigger_no_progress_detection()
    {
        var transientFailure = AgentToolResult.Failure(
            "Temporary timeout.",
            classification: AgentToolFailureClassification.Transient);
        var tool = new CountingResultTool("read", transientFailure);
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{}"),
            ToolResponse("call-2", "read", "{}"),
            FinalResponse("recovered"));
        var loop = CreateLoop(client, [tool]);

        var result = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()));

        Assert.Equal("recovered", result.Text);
        Assert.Equal(2, tool.ExecutionCount);
    }

    [Fact]
    public async Task Canonical_json_property_order_is_ignored_by_tool_call_fingerprint()
    {
        var tool = new CountingResultTool(
            "glob",
            AgentToolResult.Failure(
                "Rejected.",
                classification: AgentToolFailureClassification.Deterministic));
        var client = new ScriptedClient(
            ToolResponse("call-1", "glob", "{\"path\":\"src\",\"pattern\":\"*.cs\"}"),
            ToolResponse("call-2", "glob", "{ \"pattern\" : \"*.cs\", \"path\" : \"src\" }"));
        var loop = CreateLoop(client, [tool]);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory())));

        Assert.Equal(AgentLoopErrorCode.NoProgress, exception.Code);
        Assert.Equal(2, tool.ExecutionCount);
    }

    [Fact]
    public async Task Tool_call_id_is_ignored_by_tool_call_fingerprint()
    {
        var tool = new CountingResultTool(
            "read",
            AgentToolResult.Failure(
                "Rejected.",
                classification: AgentToolFailureClassification.Deterministic));
        var client = new ScriptedClient(
            ToolResponse("first-id", "read", "{\"path\":\"file.txt\"}"),
            ToolResponse("different-id", "read", "{\"path\":\"file.txt\"}"));
        var loop = CreateLoop(client, [tool]);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory())));

        Assert.Equal(AgentLoopErrorCode.NoProgress, exception.Code);
        Assert.Equal(2, tool.ExecutionCount);
    }

    [Fact]
    public async Task Repeated_invalid_json_stops_without_invoking_the_tool()
    {
        var tool = new CountingResultTool("read", AgentToolResult.Success("unused"));
        var observer = new RecordingObserver();
        var client = new ScriptedClient(
            ToolResponse("call-1", "read", "{"),
            ToolResponse("call-2", "read", "{"));
        var loop = CreateLoop(client, [tool]);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer));

        Assert.Equal(AgentLoopErrorCode.NoProgress, exception.Code);
        Assert.Contains("Invalid JSON arguments", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Equal(2, observer.ToolTurns.Count);
    }

    [Fact]
    public async Task Provider_failure_is_wrapped_with_clear_agent_error()
    {
        var providerFailure = new ModelProviderException(
            ModelProviderErrorCode.Unavailable,
            "provider unavailable");
        var loop = CreateLoop(new ThrowingClient(providerFailure), []);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory())));

        Assert.Equal(AgentLoopErrorCode.ProviderFailure, exception.Code);
        Assert.Same(providerFailure, exception.InnerException);
    }

    [Fact]
    public async Task Maximum_iterations_produces_clear_application_error()
    {
        var client = new ScriptedClient(
            ToolResponse("call-1", "echo", "{}"),
            ToolResponse("call-2", "echo", "{}"));
        var loop = CreateLoop(
            client,
            [new RecordingTool("echo", AgentToolResult.Success("ok"))],
            maxIterations: 2);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory())));

        Assert.Equal(AgentLoopErrorCode.MaxIterationsReached, exception.Code);
    }

    [Fact]
    public async Task Deferred_permission_runtime_guard_denies_missing_contract_without_executing_tool()
    {
        var tool = new MissingDeferredContractTool();
        var observer = new RecordingObserver();
        var loop = new AgentPulse.Application.AgentLoop.AgentLoop(
            new ScriptedClient(
                ToolResponse("call-deferred", "deferred", "{}"),
                FinalResponse("recovered")),
            new BypassRegistry(tool),
            new AgentToolOptions { ToolTimeout = TimeSpan.FromSeconds(5) },
            NullLogger<AgentPulse.Application.AgentLoop.AgentLoop>.Instance);

        var response = await loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory()),
            observer);

        Assert.Equal("recovered", response.Text);
        Assert.Equal(0, tool.ExecutionCount);
        var execution = Assert.Single(Assert.Single(observer.ToolTurns));
        Assert.False(execution.Result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, execution.Result.FailureClassification);
        Assert.Contains(
            "Deferred permission authorization is not configured",
            execution.Result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_model_response_is_rejected()
    {
        var client = new ScriptedClient(new ChatModelResponse(
            null,
            [],
            ModelFinishReason.Unknown));
        var loop = CreateLoop(client, []);

        var exception = await Assert.ThrowsAsync<AgentLoopException>(() => loop.ExecuteAsync(
            new AgentLoopRequest(InitialMessages, Directory.GetCurrentDirectory())));

        Assert.Equal(AgentLoopErrorCode.InvalidResponse, exception.Code);
    }

    private static AgentPulse.Application.AgentLoop.AgentLoop CreateLoop(
        IChatModelClient client,
        IEnumerable<IAgentTool> tools,
        int maxIterations = 8)
    {
        return new AgentPulse.Application.AgentLoop.AgentLoop(
            client,
            new AgentToolRegistry(tools),
            new AgentToolOptions
            {
                MaxToolIterations = maxIterations,
                ToolTimeout = TimeSpan.FromSeconds(5),
            },
            NullLogger<AgentPulse.Application.AgentLoop.AgentLoop>.Instance);
    }

    private static ChatModelResponse ToolResponse(
        string id,
        string name,
        string arguments)
    {
        return new ChatModelResponse(
            null,
            [new ChatModelToolCall(id, name, arguments, 1)],
            ModelFinishReason.ToolCalls);
    }

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

    private sealed class MissingDeferredContractTool : IAgentTool, IDeferredPermissionAgentTool
    {
        public string Name => "deferred";

        public string Description => "deferred";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public IDeferredPermissionExecutionContract DeferredPermissionContract => null!;

        public int ExecutionCount { get; private set; }

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(AgentToolResult.Success("unsafe"));
        }
    }

    private sealed class BypassRegistry(IAgentTool tool) : IAgentToolRegistry
    {
        public bool TryGet(string name, out IAgentTool? resolved)
        {
            resolved = string.Equals(name, tool.Name, StringComparison.Ordinal) ? tool : null;
            return resolved is not null;
        }

        public IReadOnlyList<ChatModelToolDefinition> GetDefinitions() => [];
    }

    private sealed class RecordingTool(string name, AgentToolResult result) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "test tool";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class CountingResultTool(string name, AgentToolResult result) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "counting result";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public int ExecutionCount { get; private set; }

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class QueueResultTool(string name, params AgentToolResult[] results) : IAgentTool
    {
        private readonly Queue<AgentToolResult> _results = new(results);

        public string Name { get; } = name;

        public string Description => "queued result";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public int ExecutionCount { get; private set; }

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class WaitingTool : IAgentTool
    {
        public string Name => "wait";

        public string Description => "wait";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public async Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return AgentToolResult.Success("unused");
        }
    }

    private sealed class ThrowingClient(Exception exception) : IChatModelClient
    {
        public Task<ChatModelResponse> CompleteAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken) => Task.FromException<ChatModelResponse>(exception);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ChatModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class CallbackTool(string name, Action callback) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "callback";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            callback();
            return Task.FromResult(AgentToolResult.Success("ok"));
        }
    }

    private sealed class ThrowingTool : IAgentTool
    {
        public string Name => "broken";

        public string Description => "broken";

        public string ParametersJsonSchema => "{\"type\":\"object\"}";

        public Task<AgentToolResult> ExecuteAsync(
            JsonElement arguments,
            AgentToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class RecordingObserver : IAgentLoopObserver
    {
        public List<IReadOnlyList<AgentLoopToolExecution>> ToolTurns { get; } = [];
        private List<AgentLoopToolExecution>? _currentTurn;

        public Task RecordAssistantResponseAsync(
            ChatModelResponse response,
            int iteration,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordToolResultAsync(
            AgentLoopToolExecution result,
            int iteration,
            CancellationToken cancellationToken)
        {
            _currentTurn ??= [];
            _currentTurn.Add(result);
            return Task.CompletedTask;
        }

        public Task CompleteToolTurnAsync(int iteration, CancellationToken cancellationToken)
        {
            ToolTurns.Add(_currentTurn ?? []);
            _currentTurn = null;
            return Task.CompletedTask;
        }
    }
}
