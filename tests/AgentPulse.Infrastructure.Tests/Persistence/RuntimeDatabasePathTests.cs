using AgentPulse.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class RuntimeDatabasePathTests
{
    [Fact]
    public async Task Runtime_database_uses_injected_user_scope_with_foreign_keys_and_migrations()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AgentPulse Runtime Database Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var pathProvider = new ApplicationDataPathProvider(root);
            var databasePath = pathProvider.ResolveDatabasePath(null);
            var services = new ServiceCollection();
            services.AddSingleton<IApplicationDataPathProvider>(pathProvider);
            services.AddAgentPulsePersistence(databasePath);

            await using var serviceProvider = services.BuildServiceProvider();
            await serviceProvider
                .GetRequiredService<IAgentPulseDatabaseInitializer>()
                .InitializeAsync();

            Assert.True(File.Exists(databasePath));
            Assert.Contains(
                Path.Combine("AgentPulse", "data", "agentpulse.db"),
                databasePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                $"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}",
                databasePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                databasePath,
                StringComparison.OrdinalIgnoreCase);

            var factory = serviceProvider.GetRequiredService<IDbContextFactory<AgentPulseDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
            Assert.Contains("20260627120000_InitialDomainPersistence", appliedMigrations);
            Assert.Contains("20260627150000_AddSessionRunLifecycle", appliedMigrations);
            Assert.Contains("20260627200000_AddRunMessageMetadata", appliedMigrations);
            Assert.Contains("20260628180000_AddToolCallingMessages", appliedMigrations);

            var connectionString = context.Database.GetConnectionString();
            Assert.NotNull(connectionString);
            var connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString);
            Assert.Equal(databasePath, connectionStringBuilder.DataSource);
            Assert.True(connectionStringBuilder.ForeignKeys);

            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            var foreignKeys = Convert.ToInt64(
                await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1, foreignKeys);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
