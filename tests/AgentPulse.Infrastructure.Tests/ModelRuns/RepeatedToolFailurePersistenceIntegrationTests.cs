using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using AgentPulse.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Infrastructure.Tests.ModelRuns;

public sealed class RepeatedToolFailurePersistenceIntegrationTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task No_progress_persists_complete_tool_turns_and_session_can_continue()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        using var workspace = new TemporaryDirectory();
        var projectContext = new ProjectContext(
            workspace.Path,
            workspace.Path,
            false,
            null,
            ProjectPlatform.Linux,
            UtcNow.Date,
            ProjectId.New());
        const string reason = "The requested file does not exist.";
        var failingTool = new CountingTool(
            AgentToolResult.Failure(
                reason,
                classification: AgentToolFailureClassification.Deterministic));
        var firstClient = new ScriptedClient(
            ToolResponse("call-1"),
            ToolResponse("call-2"));

        SessionId sessionId;
        await using (var failedRun = CreateService(
                         database,
                         projectContext,
                         firstClient,
                         [failingTool]))
        {
            var exception = await Assert.ThrowsAsync<ModelRunException>(() =>
                failedRun.Service.ExecuteAsync(
                    new RunPromptRequest("first prompt", workspace.Path)));

            Assert.Equal(ModelRunErrorCode.AgentNoProgress, exception.Code);
            Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "configured maximum",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(2, failingTool.ExecutionCount);

        await using (var failedState = database.CreateContext())
        {
            var messages = await failedState.Messages
                .Include(message => message.Parts.OrderBy(part => part.Order))
                .OrderBy(message => message.Sequence)
                .ToListAsync();
            var assistantToolTurns = messages
                .Where(message => message.Role == MessageRole.Assistant &&
                                  message.Parts.OfType<ToolCallMessagePart>().Any())
                .ToArray();
            var toolResultMessages = messages
                .Where(message => message.Role == MessageRole.Tool)
                .ToArray();

            Assert.Equal(2, assistantToolTurns.Length);
            Assert.Equal(2, toolResultMessages.Length);
            Assert.All(assistantToolTurns, message => Assert.Equal(MessageStatus.Completed, message.Status));
            Assert.All(toolResultMessages, message => Assert.Equal(MessageStatus.Completed, message.Status));

            foreach (var assistant in assistantToolTurns)
            {
                var call = Assert.Single(assistant.Parts.OfType<ToolCallMessagePart>());
                var result = Assert.Single(
                    toolResultMessages.SelectMany(message =>
                        message.Parts.OfType<ToolResultMessagePart>()),
                    value =>
                        value.AssistantToolCallMessageId == assistant.Id &&
                        string.Equals(value.ToolCallId, call.ToolCallId, StringComparison.Ordinal));
                Assert.Equal(call.ToolName, result.ToolName);
                Assert.False(result.Succeeded);
                Assert.Equal(reason, result.Error);
            }

            var failedAssistant = Assert.Single(messages, message =>
                message.Role == MessageRole.Assistant &&
                message.Status == MessageStatus.Failed);
            Assert.Equal("NoProgress", failedAssistant.FailureKind);
            Assert.Contains(
                reason,
                failedAssistant.FailureReason ?? string.Empty,
                StringComparison.Ordinal);

            var session = await failedState.Sessions.SingleAsync();
            sessionId = session.Id;
            Assert.Equal(SessionStatus.Idle, session.Status);
            Assert.Empty(await failedState.RunLeases.ToListAsync());
        }

        var continuationClient = new ScriptedClient(FinalResponse("continued"));
        await using (var continuation = CreateService(
                         database,
                         projectContext,
                         continuationClient,
                         [failingTool]))
        {
            var result = await continuation.Service.ExecuteAsync(
                new RunPromptRequest(
                    "second prompt",
                    workspace.Path,
                    sessionId));

            Assert.Equal("continued", result.Text);
        }

        var continuationRequest = Assert.Single(continuationClient.Requests);
        Assert.Equal(
            [
                ChatModelRole.System,
                ChatModelRole.User,
                ChatModelRole.Assistant,
                ChatModelRole.Tool,
                ChatModelRole.Assistant,
                ChatModelRole.Tool,
                ChatModelRole.User,
            ],
            continuationRequest.Messages.Select(message => message.Role));
        Assert.Equal(
            ["call-1", "call-2"],
            continuationRequest.Messages
                .Where(message => message.Role == ChatModelRole.Tool)
                .Select(message => message.ToolCallId));

        await using var finalState = database.CreateContext();
        Assert.Equal(SessionStatus.Idle, (await finalState.Sessions.SingleAsync()).Status);
        Assert.Empty(await finalState.RunLeases.ToListAsync());
    }

    private static ToolCallingService CreateService(
        SqliteTestDatabase database,
        ProjectContext projectContext,
        IChatModelClient client,
        IEnumerable<IAgentTool> tools)
    {
        var preparationContext = database.CreateContext();
        var clock = new FixedClock();
        var dbContextFactory = new TestDbContextFactory(database.Options);
        var sessionOptions = new SessionRunOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(5),
        };
        var streamingOptions = new StreamingRunOptions
        {
            FlushInterval = TimeSpan.FromHours(1),
            FlushCharacterThreshold = 256,
            LeaseRenewInterval = TimeSpan.FromMinutes(1),
            CleanupTimeout = TimeSpan.FromSeconds(5),
        };
        var prepare = new PrepareSessionRun(
            new ProjectRepository(preparationContext),
            new SessionRepository(preparationContext),
            new MessageRepository(preparationContext),
            new RunLeaseRepository(preparationContext),
            new UnitOfWork(preparationContext),
            clock,
            sessionOptions);
        var end = new EndSessionRun(
            new SessionRepository(preparationContext),
            new RunLeaseRepository(preparationContext),
            new UnitOfWork(preparationContext),
            clock);
        var loop = new AgentPulse.Application.AgentLoop.AgentLoop(
            client,
            new AgentToolRegistry(tools),
            new AgentToolOptions
            {
                MaxToolIterations = 8,
                ToolTimeout = TimeSpan.FromSeconds(5),
            },
            NullLogger<AgentPulse.Application.AgentLoop.AgentLoop>.Instance);
        var service = new ToolCallingRunPrompt(
            new StubProjectContextFactory(projectContext),
            prepare,
            new ChatModelRequestBuilder(new ChatModelHistoryPolicy()),
            loop,
            new AgentToolTurnPersistence(
                dbContextFactory,
                clock,
                NullLogger<AgentToolTurnPersistence>.Instance),
            new StreamingRunPersistence(dbContextFactory, clock),
            new RunLeaseRenewalService(dbContextFactory, clock, sessionOptions),
            end,
            new NullOutputSink(),
            new ChatModelRunDefaults("test-model"),
            streamingOptions,
            NullLogger<ToolCallingRunPrompt>.Instance);
        return new ToolCallingService(service, preparationContext);
    }

    private static ChatModelResponse ToolResponse(string id) =>
        new(
            null,
            [new ChatModelToolCall(id, "read", "{\"path\":\"missing.txt\"}", 1)],
            ModelFinishReason.ToolCalls);

    private static ChatModelResponse FinalResponse(string text) =>
        new(text, [], ModelFinishReason.Stop);

    private sealed class CountingTool(AgentToolResult result) : IAgentTool
    {
        public string Name => "read";

        public string Description => "Return a controlled test result.";

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

    private sealed class StubProjectContextFactory(ProjectContext context)
        : IProjectContextFactory
    {
        public Task<ProjectContext> CreateAsync(
            string? inputPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(context);
        }
    }

    private sealed class NullOutputSink : IModelOutputSink
    {
        public Task WriteDeltaAsync(
            string delta,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<AgentPulseDbContext> options)
        : IDbContextFactory<AgentPulseDbContext>
    {
        public AgentPulseDbContext CreateDbContext() => new(options);

        public Task<AgentPulseDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => RepeatedToolFailurePersistenceIntegrationTests.UtcNow;
    }

    private sealed class ToolCallingService(
        ToolCallingRunPrompt service,
        AgentPulseDbContext context) : IAsyncDisposable
    {
        public ToolCallingRunPrompt Service { get; } = service;

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AgentPulse.Tests",
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
