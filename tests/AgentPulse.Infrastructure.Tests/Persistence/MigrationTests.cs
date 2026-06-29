using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class MigrationTests
{
    private const string ToolCallingMigration = "20260628180000_AddToolCallingMessages";
    private const string ToolResultIntegrityMigration = "20260628213000_EnforceToolResultTurnIntegrity";

    [Fact]
    public async Task Empty_database_is_created_by_all_migrations()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();

        Assert.Equal(5, (await context.Database.GetPendingMigrationsAsync()).Count());

        await context.Database.MigrateAsync();

        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20260627120000_InitialDomainPersistence", appliedMigrations);
        Assert.Contains("20260627150000_AddSessionRunLifecycle", appliedMigrations);
        Assert.Contains("20260627200000_AddRunMessageMetadata", appliedMigrations);
        Assert.Contains(ToolCallingMigration, appliedMigrations);
        Assert.Contains(ToolResultIntegrityMigration, appliedMigrations);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(nameof(InitialDomainPersistence), typeof(InitialDomainPersistence).Name);
    }

    [Fact]
    public async Task Model_snapshot_has_no_pending_schema_changes()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }


    [Fact]
    public async Task Integrity_migration_can_be_rolled_back()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();

        await MigrateToLatestAsync(context);
        Assert.True(await IndexExistsAsync(context, "UX_MessageParts_ToolResult_Session_Turn_Call"));

        await MigrateToToolCallingAsync(context);

        Assert.False(await IndexExistsAsync(context, "UX_MessageParts_ToolResult_Session_Turn_Call"));
        Assert.DoesNotContain(ToolResultIntegrityMigration, await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(
            0,
            await ScalarAsync<long>(
                context,
                "SELECT COUNT(*) FROM pragma_table_info('MessageParts') WHERE name IN ('ToolResultSessionId', 'AssistantToolCallMessageId');"));
    }

    [Fact]
    public async Task Integrity_migration_deduplicates_only_identical_tool_results_and_creates_unique_index()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();
        await MigrateToToolCallingAsync(context);

        var sessionId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        await SeedSessionAsync(context, sessionId);
        await SeedAssistantToolCallAsync(context, sessionId, assistantId, 1, "call-1", "read");
        var keptPartId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var duplicatePartId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        await SeedToolResultAsync(
            context, sessionId, assistantId, 2, "call-1", "read", true, "ok", null,
            "{\"path\":\"README.md\"}", keptPartId);
        await SeedToolResultAsync(
            context, sessionId, assistantId, 3, "call-1", "read", true, "ok", null,
            "{\"path\":\"README.md\"}", duplicatePartId);

        await MigrateToLatestAsync(context);

        Assert.Equal(1, await CountToolResultsAsync(context));
        Assert.Equal(
            keptPartId.ToString(),
            await ScalarAsync<string>(
                context,
                "SELECT Id FROM MessageParts WHERE PartType = 'tool_result' LIMIT 1;"));
        Assert.True(await IndexExistsAsync(context, "UX_MessageParts_ToolResult_Session_Turn_Call"));

        var duplicateId = Guid.NewGuid();
        var existingToolMessageId = await ScalarAsync<string>(
            context,
            "SELECT MessageId FROM MessageParts WHERE PartType = 'tool_result' LIMIT 1;");
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO MessageParts (
                    Id, MessageId, "Order", CreatedAtUtc, UpdatedAtUtc, PartType,
                    ToolCallId, ToolName, Succeeded, Output, Error, MetadataJson,
                    ToolResultSessionId, AssistantToolCallMessageId)
                VALUES (
                    {duplicateId}, {existingToolMessageId}, 2, 0, 0, 'tool_result',
                    {"call-1"}, {"read"}, {true}, {"ok"}, {(string?)null}, {"{\"path\":\"README.md\"}"},
                    {sessionId}, {assistantId});
                """));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task Integrity_migration_fails_without_deleting_conflicting_outputs()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();
        await MigrateToToolCallingAsync(context);

        var sessionId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        await SeedSessionAsync(context, sessionId);
        await SeedAssistantToolCallAsync(context, sessionId, assistantId, 1, "call-1", "read");
        await SeedToolResultAsync(context, sessionId, assistantId, 2, "call-1", "read", true, "first", null, null);
        await SeedToolResultAsync(context, sessionId, assistantId, 3, "call-1", "read", true, "second", null, null);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToLatestAsync(context));

        Assert.Contains("Conflicting tool results exist", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await CountToolResultsAsync(context));
        Assert.DoesNotContain(ToolResultIntegrityMigration, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Integrity_migration_fails_without_deleting_conflicting_success_states()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();
        await MigrateToToolCallingAsync(context);

        var sessionId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        await SeedSessionAsync(context, sessionId);
        await SeedAssistantToolCallAsync(context, sessionId, assistantId, 1, "call-1", "read");
        await SeedToolResultAsync(context, sessionId, assistantId, 2, "call-1", "read", true, "ok", null, null);
        await SeedToolResultAsync(context, sessionId, assistantId, 3, "call-1", "read", false, string.Empty, "failed", null);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToLatestAsync(context));

        Assert.Contains("Conflicting tool results exist", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await CountToolResultsAsync(context));
        Assert.DoesNotContain(ToolResultIntegrityMigration, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Same_tool_call_id_in_different_sessions_is_preserved()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();
        await MigrateToToolCallingAsync(context);

        foreach (var sequenceSeed in new[] { 1, 10 })
        {
            var sessionId = Guid.NewGuid();
            var assistantId = Guid.NewGuid();
            await SeedSessionAsync(context, sessionId);
            await SeedAssistantToolCallAsync(context, sessionId, assistantId, sequenceSeed, "call-1", "read");
            await SeedToolResultAsync(context, sessionId, assistantId, sequenceSeed + 1, "call-1", "read", true, "ok", null, null);
        }

        await MigrateToLatestAsync(context);

        Assert.Equal(2, await CountToolResultsAsync(context));
    }

    [Fact]
    public async Task Same_tool_call_id_in_different_assistant_turns_is_preserved()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();
        await MigrateToToolCallingAsync(context);

        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(context, sessionId);
        var firstAssistantId = Guid.NewGuid();
        await SeedAssistantToolCallAsync(context, sessionId, firstAssistantId, 1, "call-1", "read");
        await SeedToolResultAsync(context, sessionId, firstAssistantId, 2, "call-1", "read", true, "first", null, null);
        var secondAssistantId = Guid.NewGuid();
        await SeedAssistantToolCallAsync(context, sessionId, secondAssistantId, 3, "call-1", "read");
        await SeedToolResultAsync(context, sessionId, secondAssistantId, 4, "call-1", "read", true, "second", null, null);

        await MigrateToLatestAsync(context);

        Assert.Equal(2, await CountToolResultsAsync(context));
        Assert.Equal(
            2,
            await ScalarAsync<long>(
                context,
                "SELECT COUNT(DISTINCT AssistantToolCallMessageId) FROM MessageParts WHERE PartType = 'tool_result';"));
    }

    private static async Task MigrateToToolCallingAsync(AgentPulseDbContext context)
    {
        await context.GetService<IMigrator>().MigrateAsync(ToolCallingMigration);
    }

    private static async Task MigrateToLatestAsync(AgentPulseDbContext context)
    {
        await context.GetService<IMigrator>().MigrateAsync(ToolResultIntegrityMigration);
    }

    private static async Task SeedSessionAsync(AgentPulseDbContext context, Guid sessionId)
    {
        var projectId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Projects (Id, NormalizedRootPath, IsGitRepository, GitWorktree, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({projectId}, {$"/workspace/{projectId:D}"}, {false}, {(string?)null}, 0, 0);
            INSERT INTO Sessions (Id, ProjectId, Status, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({sessionId}, {projectId}, {"Idle"}, 0, 0);
            """);
    }

    private static async Task SeedAssistantToolCallAsync(
        AgentPulseDbContext context,
        Guid sessionId,
        Guid assistantMessageId,
        long sequence,
        string toolCallId,
        string toolName)
    {
        var callPartId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Messages (Id, SessionId, Role, Status, Sequence, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({assistantMessageId}, {sessionId}, {"Assistant"}, {"Completed"}, {sequence}, 0, 0);
            INSERT INTO MessageParts (
                Id, MessageId, "Order", CreatedAtUtc, UpdatedAtUtc, PartType,
                ToolCallId, ToolName, ArgumentsJson)
            VALUES (
                {callPartId}, {assistantMessageId}, 1, 0, 0, 'tool_call',
                {toolCallId}, {toolName}, {"{}"});
            """);
    }

    private static async Task SeedToolResultAsync(
        AgentPulseDbContext context,
        Guid sessionId,
        Guid assistantMessageId,
        long sequence,
        string toolCallId,
        string toolName,
        bool succeeded,
        string output,
        string? error,
        string? metadataJson,
        Guid? messagePartId = null)
    {
        var toolMessageId = Guid.NewGuid();
        var resultPartId = messagePartId ?? Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Messages (Id, SessionId, Role, Status, Sequence, CreatedAtUtc, UpdatedAtUtc)
            VALUES ({toolMessageId}, {sessionId}, {"Tool"}, {"Completed"}, {sequence}, 0, 0);
            INSERT INTO MessageParts (
                Id, MessageId, "Order", CreatedAtUtc, UpdatedAtUtc, PartType,
                ToolCallId, ToolName, Succeeded, Output, Error, MetadataJson)
            VALUES (
                {resultPartId}, {toolMessageId}, 1, 0, 0, 'tool_result',
                {toolCallId}, {toolName}, {succeeded}, {output}, {error}, {metadataJson});
            """);
    }

    private static Task<int> CountToolResultsAsync(AgentPulseDbContext context)
    {
        return ScalarAsync<int>(
            context,
            "SELECT COUNT(*) FROM MessageParts WHERE PartType = 'tool_result';");
    }

    private static async Task<bool> IndexExistsAsync(AgentPulseDbContext context, string indexName)
    {
        return await ScalarAsync<long>(
            context,
            $"SELECT COUNT(*) FROM pragma_index_list('MessageParts') WHERE name = '{indexName}';") == 1;
    }

    private static async Task<T> ScalarAsync<T>(AgentPulseDbContext context, string commandText)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("The scalar query returned no value.");
        }

        var converted = Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
        return (T)(converted ?? throw new InvalidOperationException("The scalar value could not be converted."));
    }
}
