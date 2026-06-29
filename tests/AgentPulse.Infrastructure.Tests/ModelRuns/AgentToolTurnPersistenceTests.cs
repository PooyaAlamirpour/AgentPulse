using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using AgentPulse.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.ModelRuns;

public sealed class AgentToolTurnPersistenceTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Tool_turn_and_final_response_are_persisted_in_reconstructable_sequence()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FixedClock();
        var factory = new TestDbContextFactory(database.Options);
        var projectContext = new ProjectContext(
            "/workspace/project",
            "/workspace/project",
            false,
            null,
            ProjectPlatform.Linux,
            UtcNow.Date,
            ProjectId.New());

        PrepareSessionRunResult prepared;
        await using (var preparationContext = database.CreateContext())
        {
            prepared = await CreatePrepare(preparationContext, clock).ExecuteAsync(
                new PrepareSessionRunRequest(
                    projectContext,
                    null,
                    "Inspect README.md",
                    "test-model"));
        }

        var call = new ChatModelToolCall(
            "call-read-1",
            "read",
            "{\"path\":\"README.md\"}",
            1);
        var response = new ChatModelResponse(
            "I will inspect the file.",
            [call],
            ModelFinishReason.ToolCalls,
            new ModelUsage(10, 3, 13));
        var execution = new AgentLoopToolExecution(
            call,
            AgentToolResult.Success(
                "1: # AgentPulse",
                new Dictionary<string, string> { ["path"] = "README.md" }),
            TimeSpan.FromMilliseconds(12));
        var toolPersistence = new AgentToolTurnPersistence(factory, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentToolTurnPersistence>.Instance);

        await toolPersistence.SaveAssistantToolCallsAsync(
            prepared.Session.Id,
            prepared.AssistantMessage.Id,
            prepared.RunLease.LeaseId,
            "test-model",
            response);
        await toolPersistence.SaveToolResultAsync(
            prepared.Session.Id,
            prepared.AssistantMessage.Id,
            prepared.RunLease.LeaseId,
            execution);
        var finalAssistantId = await toolPersistence.StartNextAssistantMessageAsync(
            prepared.Session.Id,
            prepared.RunLease.LeaseId,
            "test-model");

        var streamingPersistence = new StreamingRunPersistence(factory, clock);
        await streamingPersistence.CompleteAsync(
            prepared.Session.Id,
            finalAssistantId,
            prepared.RunLease.LeaseId,
            "The project is a .NET command-line agent.",
            new AgentPulse.Application.ModelRuns.AssistantCompletionMetadata(
                "test-model",
                ModelFinishReason.Stop,
                new ModelUsage(20, 8, 28)));

        await using var verification = database.CreateContext();
        var messages = await verification.Messages
            .Include(message => message.Parts.OrderBy(part => part.Order))
            .OrderBy(message => message.Sequence)
            .ToArrayAsync();

        Assert.Equal(4, messages.Length);
        Assert.Equal(
            [MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.Assistant],
            messages.Select(message => message.Role));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, messages.Select(message => message.Sequence));

        var assistantCallMessage = messages[1];
        Assert.Equal(MessageStatus.Completed, assistantCallMessage.Status);
        Assert.Equal(ModelFinishReason.ToolCalls.ToString(), assistantCallMessage.FinishReason);
        Assert.Equal("I will inspect the file.", Assert.IsType<TextMessagePart>(assistantCallMessage.Parts.First()).Text);
        var persistedCall = Assert.IsType<ToolCallMessagePart>(assistantCallMessage.Parts.Last());
        Assert.Equal(call.Id, persistedCall.ToolCallId);
        Assert.Equal(call.Name, persistedCall.ToolName);
        Assert.Equal(call.ArgumentsJson, persistedCall.ArgumentsJson);

        var toolMessage = messages[2];
        Assert.Equal(MessageStatus.Completed, toolMessage.Status);
        var persistedResult = Assert.IsType<ToolResultMessagePart>(Assert.Single(toolMessage.Parts));
        Assert.Equal(call.Id, persistedResult.ToolCallId);
        Assert.Equal(call.Name, persistedResult.ToolName);
        Assert.True(persistedResult.Succeeded);
        Assert.Equal("1: # AgentPulse", persistedResult.Output);
        Assert.Null(persistedResult.Error);

        var finalAssistant = messages[3];
        Assert.Equal(finalAssistantId, finalAssistant.Id);
        Assert.Equal(MessageStatus.Completed, finalAssistant.Status);
        Assert.Equal(ModelFinishReason.Stop.ToString(), finalAssistant.FinishReason);
        Assert.Equal(
            "The project is a .NET command-line agent.",
            Assert.IsType<TextMessagePart>(Assert.Single(finalAssistant.Parts)).Text);
    }


    [Fact]
    public async Task Same_tool_call_id_is_idempotent_per_session_not_global()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FixedClock();
        var factory = new TestDbContextFactory(database.Options);
        var projectContext = new ProjectContext(
            "/workspace/project",
            "/workspace/project",
            false,
            null,
            ProjectPlatform.Linux,
            UtcNow.Date,
            ProjectId.New());

        PrepareSessionRunResult first;
        PrepareSessionRunResult second;
        await using (var context = database.CreateContext())
        {
            var prepare = CreatePrepare(context, clock);
            first = await prepare.ExecuteAsync(new PrepareSessionRunRequest(
                projectContext, null, "first", "test-model"));
        }
        await using (var context = database.CreateContext())
        {
            second = await CreatePrepare(context, clock).ExecuteAsync(new PrepareSessionRunRequest(
                projectContext, null, "second", "test-model"));
        }

        var call = new ChatModelToolCall("call-1", "read", "{\"path\":\"README.md\"}", 1);
        var response = new ChatModelResponse(null, [call], ModelFinishReason.ToolCalls);
        var execution = new AgentLoopToolExecution(
            call, AgentToolResult.Success("ok"), TimeSpan.FromMilliseconds(1));
        var persistence = new AgentToolTurnPersistence(factory, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentToolTurnPersistence>.Instance);

        foreach (var prepared in new[] { first, second })
        {
            await persistence.SaveAssistantToolCallsAsync(
                prepared.Session.Id, prepared.AssistantMessage.Id, prepared.RunLease.LeaseId,
                "test-model", response);
            await persistence.SaveToolResultAsync(prepared.Session.Id, prepared.AssistantMessage.Id, prepared.RunLease.LeaseId, execution);
            await persistence.SaveToolResultAsync(prepared.Session.Id, prepared.AssistantMessage.Id, prepared.RunLease.LeaseId, execution);
        }

        await using var verification = database.CreateContext();
        var results = await verification.MessageParts.OfType<ToolResultMessagePart>()
            .Join(verification.Messages, part => part.MessageId, message => message.Id,
                (part, message) => new { part.ToolCallId, message.SessionId })
            .ToArrayAsync();

        Assert.Equal(2, results.Length);
        Assert.All(results, value => Assert.Equal("call-1", value.ToolCallId));
        Assert.Equal(2, results.Select(value => value.SessionId).Distinct().Count());
    }


    [Fact]
    public async Task Concurrent_duplicate_tool_results_are_idempotent()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FixedClock();
        var factory = new TestDbContextFactory(database.Options);
        PrepareSessionRunResult prepared;
        await using (var context = database.CreateContext())
        {
            prepared = await CreatePrepare(context, clock).ExecuteAsync(new PrepareSessionRunRequest(
                new ProjectContext("/workspace/project", "/workspace/project", false, null,
                    ProjectPlatform.Linux, UtcNow.Date, ProjectId.New()),
                null, "race", "test-model"));
        }

        var call = new ChatModelToolCall("call-race", "read", "{}", 1);
        var persistence = new AgentToolTurnPersistence(
            factory, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentToolTurnPersistence>.Instance);
        await persistence.SaveAssistantToolCallsAsync(
            prepared.Session.Id, prepared.AssistantMessage.Id, prepared.RunLease.LeaseId,
            "test-model", new ChatModelResponse(null, [call], ModelFinishReason.ToolCalls));
        var execution = new AgentLoopToolExecution(call, AgentToolResult.Success("ok"), TimeSpan.Zero);

        await Task.WhenAll(
            persistence.SaveToolResultAsync(prepared.Session.Id, prepared.AssistantMessage.Id,
                prepared.RunLease.LeaseId, execution),
            persistence.SaveToolResultAsync(prepared.Session.Id, prepared.AssistantMessage.Id,
                prepared.RunLease.LeaseId, execution));

        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.MessageParts.OfType<ToolResultMessagePart>().CountAsync());
    }

    [Fact]
    public async Task Tool_result_is_rejected_for_unknown_session_call_or_mismatched_name()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var clock = new FixedClock();
        var factory = new TestDbContextFactory(database.Options);
        PrepareSessionRunResult prepared;
        await using (var context = database.CreateContext())
        {
            prepared = await CreatePrepare(context, clock).ExecuteAsync(new PrepareSessionRunRequest(
                new ProjectContext("/workspace/project", "/workspace/project", false, null,
                    ProjectPlatform.Linux, UtcNow.Date, ProjectId.New()),
                null, "validation", "test-model"));
        }

        var call = new ChatModelToolCall("call-1", "read", "{}", 1);
        var persistence = new AgentToolTurnPersistence(
            factory, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentToolTurnPersistence>.Instance);
        await persistence.SaveAssistantToolCallsAsync(
            prepared.Session.Id, prepared.AssistantMessage.Id, prepared.RunLease.LeaseId,
            "test-model", new ChatModelResponse(null, [call], ModelFinishReason.ToolCalls));

        await Assert.ThrowsAsync<SessionRunException>(() => persistence.SaveToolResultAsync(
            SessionId.New(), prepared.AssistantMessage.Id, prepared.RunLease.LeaseId,
            new AgentLoopToolExecution(call, AgentToolResult.Success("ok"), TimeSpan.Zero)));
        await Assert.ThrowsAsync<SessionRunException>(() => persistence.SaveToolResultAsync(
            prepared.Session.Id, prepared.AssistantMessage.Id, prepared.RunLease.LeaseId,
            new AgentLoopToolExecution(new ChatModelToolCall("missing", call.Name, call.ArgumentsJson, call.Order), AgentToolResult.Success("ok"), TimeSpan.Zero)));
        await Assert.ThrowsAsync<SessionRunException>(() => persistence.SaveToolResultAsync(
            prepared.Session.Id, prepared.AssistantMessage.Id, prepared.RunLease.LeaseId,
            new AgentLoopToolExecution(new ChatModelToolCall(call.Id, "grep", call.ArgumentsJson, call.Order), AgentToolResult.Success("ok"), TimeSpan.Zero)));

        await using var verification = database.CreateContext();
        Assert.Empty(await verification.MessageParts.OfType<ToolResultMessagePart>().ToArrayAsync());
    }

    private static PrepareSessionRun CreatePrepare(
        AgentPulseDbContext context,
        IClock clock)
    {
        return new PrepareSessionRun(
            new ProjectRepository(context),
            new SessionRepository(context),
            new MessageRepository(context),
            new RunLeaseRepository(context),
            new UnitOfWork(context),
            clock,
            new SessionRunOptions { LeaseDuration = TimeSpan.FromMinutes(5) });
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
        public DateTime UtcNow => AgentToolTurnPersistenceTests.UtcNow;
    }
}
