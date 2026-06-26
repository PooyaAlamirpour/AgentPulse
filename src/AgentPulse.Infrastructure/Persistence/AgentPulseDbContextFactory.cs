using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentPulse.Infrastructure.Persistence;

public sealed class AgentPulseDbContextFactory : IDesignTimeDbContextFactory<AgentPulseDbContext>
{
    public AgentPulseDbContext CreateDbContext(string[] args)
    {
        var databasePath = Environment.GetEnvironmentVariable("AGENTPULSE_DB_PATH");

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(Directory.GetCurrentDirectory(), "agentpulse.db");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
        }.ToString();

        var options = new DbContextOptionsBuilder<AgentPulseDbContext>()
            .UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(AgentPulseDbContext).Assembly.GetName().Name!))
            .Options;

        return new AgentPulseDbContext(options);
    }
}
