using AgentPulse.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class MigrationTests
{
    [Fact]
    public async Task Empty_database_is_created_by_the_initial_migration()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(migrate: false);
        await using var context = database.CreateContext();

        Assert.Equal(2, (await context.Database.GetPendingMigrationsAsync()).Count());

        await context.Database.MigrateAsync();

        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20260627120000_InitialDomainPersistence", appliedMigrations);
        Assert.Contains("20260627150000_AddSessionRunLifecycle", appliedMigrations);
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

}
