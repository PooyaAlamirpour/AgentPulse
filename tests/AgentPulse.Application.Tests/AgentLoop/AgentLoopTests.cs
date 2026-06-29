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
        Assert.Equal("call-timeout", execution.Call.Id);
        Assert.Contains("timeout", execution.Result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(client.Requests[1].Messages, message =>
            message.Role == ChatModelRole.Tool && message.ToolCallId == "call-timeout");
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
