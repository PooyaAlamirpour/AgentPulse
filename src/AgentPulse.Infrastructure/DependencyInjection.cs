using AgentPulse.Application.Persistence;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPulse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentPulsePersistence(
        this IServiceCollection services,
        string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var absoluteDatabasePath = Path.GetFullPath(databasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absoluteDatabasePath,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        services.AddDbContext<AgentPulseDbContext>(options =>
            options.UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly(
                    typeof(AgentPulseDbContext).Assembly.GetName().Name!)));

        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IRunLeaseRepository, RunLeaseRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        return services;
    }
}
