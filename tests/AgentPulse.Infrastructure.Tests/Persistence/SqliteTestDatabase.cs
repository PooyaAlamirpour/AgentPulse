using AgentPulse.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Tests.Persistence;

internal sealed class SqliteTestDatabase : IAsyncDisposable
{
    private SqliteTestDatabase(string directoryPath, DbContextOptions<AgentPulseDbContext> options)
    {
        DirectoryPath = directoryPath;
        Options = options;
    }

    private string DirectoryPath { get; }

    public DbContextOptions<AgentPulseDbContext> Options { get; }

    public static async Task<SqliteTestDatabase> CreateAsync(bool migrate = true)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "AgentPulse.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);

        var databasePath = Path.Combine(directoryPath, "agentpulse-tests.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = false,
        }.ToString();

        var options = new DbContextOptionsBuilder<AgentPulseDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var database = new SqliteTestDatabase(directoryPath, options);

        if (migrate)
        {
            await using var context = database.CreateContext();
            await context.Database.MigrateAsync();
        }

        return database;
    }

    public AgentPulseDbContext CreateContext() => new(Options);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
