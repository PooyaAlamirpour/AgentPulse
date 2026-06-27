using AgentPulse.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class ConstraintTests
{
    [Fact]
    public async Task Duplicate_sequence_in_same_session_is_rejected()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);
        var first = PersistenceTestData.CreateMessage(session, 1, "first");
        var duplicate = PersistenceTestData.CreateMessage(session, 1, "duplicate");

        await using var context = database.CreateContext();
        await context.AddRangeAsync(project, session, first, duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Foreign_keys_are_enabled_and_invalid_reference_is_rejected()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();

        await using (var context = database.CreateContext())
        {
            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            var result = await command.ExecuteScalarAsync();

            Assert.Equal(1L, Convert.ToInt64(result));
        }

        await using (var context = database.CreateContext())
        {
            var orphan = new AgentPulse.Domain.Sessions.Session(
                AgentPulse.Domain.Sessions.SessionId.New(),
                AgentPulse.Domain.Projects.ProjectId.New(),
                PersistenceTestData.TimestampUtc);
            await context.Sessions.AddAsync(orphan);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Parent_deletion_is_restricted_instead_of_cascading()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);
        var message = PersistenceTestData.CreateMessage(session, 1, "kept");

        await using (var seedContext = database.CreateContext())
        {
            await seedContext.AddRangeAsync(project, session, message);
            await seedContext.SaveChangesAsync();
        }

        await using (var deleteContext = database.CreateContext())
        {
            var persistedProject = await deleteContext.Projects.SingleAsync();
            deleteContext.Projects.Remove(persistedProject);

            await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
        }

        await using var verificationContext = database.CreateContext();
        Assert.Equal(1, await verificationContext.Projects.CountAsync());
        Assert.Equal(1, await verificationContext.Sessions.CountAsync());
        Assert.Equal(1, await verificationContext.Messages.CountAsync());
        Assert.Equal(1, await verificationContext.MessageParts.CountAsync());
    }

    [Fact]
    public async Task Only_one_run_lease_can_exist_per_session()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);
        var firstLease = new AgentPulse.Domain.SessionRuns.RunLease(
            session.Id,
            AgentPulse.Domain.SessionRuns.RunLeaseId.New(),
            PersistenceTestData.TimestampUtc,
            PersistenceTestData.TimestampUtc.AddMinutes(5));

        await using (var seedContext = database.CreateContext())
        {
            await seedContext.AddRangeAsync(project, session, firstLease);
            await seedContext.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var duplicateLease = new AgentPulse.Domain.SessionRuns.RunLease(
            session.Id,
            AgentPulse.Domain.SessionRuns.RunLeaseId.New(),
            PersistenceTestData.TimestampUtc,
            PersistenceTestData.TimestampUtc.AddMinutes(5));
        await context.RunLeases.AddAsync(duplicateLease);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_part_order_in_same_message_is_rejected_by_database()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var project = PersistenceTestData.CreateProject();
        var session = PersistenceTestData.CreateSession(project);
        var message = PersistenceTestData.CreateMessage(session, 1, "first");

        await using (var seedContext = database.CreateContext())
        {
            await seedContext.AddRangeAsync(project, session, message);
            await seedContext.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO MessageParts
                    (Id, MessageId, "Order", CreatedAtUtc, UpdatedAtUtc, PartType, Text)
                VALUES
                    ({Guid.NewGuid()}, {message.Id.Value}, {1}, {PersistenceTestData.TimestampUtc.Ticks},
                     {PersistenceTestData.TimestampUtc.Ticks}, {"text"}, {"duplicate"});
                """));
    }
}
