using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace AgentPulse.Infrastructure.Persistence;

public sealed class AgentPulseDbContextFactory : IDesignTimeDbContextFactory<AgentPulseDbContext>
{
    private const string DesignTimeDatabaseFileName = "agentpulse-migrations.db";
    private const string RelativePathErrorMessage =
        "AgentPulse__Persistence__DatabasePath must be an absolute path when used at design time.";

    private readonly string? _configuredDatabasePath;
    private readonly string _designTimeRootPath;

    public AgentPulseDbContextFactory()
        : this(
            ResolveConfiguredDatabasePath(),
            Path.Combine(Path.GetTempPath(), "AgentPulse", "design-time"))
    {
    }

    public AgentPulseDbContextFactory(
        string? configuredDatabasePath,
        string designTimeRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(designTimeRootPath);

        _configuredDatabasePath = configuredDatabasePath;
        _designTimeRootPath = Path.GetFullPath(designTimeRootPath);
    }

    public AgentPulseDbContext CreateDbContext(string[] args)
    {
        var databasePath = ResolveDesignTimeDatabasePath();
        var databaseDirectory = Path.GetDirectoryName(databasePath);

        if (string.IsNullOrWhiteSpace(databaseDirectory))
        {
            throw new InvalidOperationException(
                "The design-time database path must include a valid directory.");
        }

        Directory.CreateDirectory(databaseDirectory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        var options = new DbContextOptionsBuilder<AgentPulseDbContext>()
            .UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly(typeof(AgentPulseDbContext).Assembly.GetName().Name!))
            .Options;

        return new AgentPulseDbContext(options);
    }

    private string ResolveDesignTimeDatabasePath()
    {
        if (string.IsNullOrWhiteSpace(_configuredDatabasePath))
        {
            return Path.GetFullPath(
                Path.Combine(_designTimeRootPath, DesignTimeDatabaseFileName));
        }

        var configuredPath = _configuredDatabasePath.Trim();
        if (!Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException(RelativePathErrorMessage);
        }

        return Path.GetFullPath(configuredPath);
    }

    private static string? ResolveConfiguredDatabasePath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(currentDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(
                Path.Combine("src", "AgentPulse.Cli", "appsettings.json"),
                optional: true,
                reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        return configuration[$"{PersistenceOptions.SectionName}:DatabasePath"];
    }
}
