using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.Persistence;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.Xiaomi;
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
        var databaseDirectory = Path.GetDirectoryName(absoluteDatabasePath);

        if (string.IsNullOrWhiteSpace(databaseDirectory))
        {
            throw new InvalidOperationException(
                "The AgentPulse database path must include a valid directory.");
        }

        Directory.CreateDirectory(databaseDirectory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absoluteDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        services.AddDbContextFactory<AgentPulseDbContext>(options =>
            options.UseSqlite(
                connectionString,
                sqlite => sqlite.MigrationsAssembly(
                    typeof(AgentPulseDbContext).Assembly.GetName().Name!)));

        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IRunLeaseRepository, RunLeaseRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddSingleton<IAgentPulseDatabaseInitializer, AgentPulseDatabaseInitializer>();
        services.AddSingleton<IStreamingRunPersistence, StreamingRunPersistence>();
        services.AddSingleton<IRunLeaseRenewalService, RunLeaseRenewalService>();

        return services;
    }

    public static IServiceCollection AddAgentPulseCredentialStore(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProviderCredentialStore, DataProtectionProviderCredentialStore>();
        services.AddScoped<IProviderCredentialSession, ProviderCredentialSession>();
        return services;
    }

    public static IServiceCollection AddAgentPulseCredentialStore(
        this IServiceCollection services,
        ProviderCredentialStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        return services.AddAgentPulseCredentialStore();
    }

    public static IServiceCollection AddXiaomiModelProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<XiaomiSseParser>();
        services.AddHttpClient(XiaomiChatModelClient.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddScoped<IChatModelClient, XiaomiChatModelClient>();
        return services;
    }

    public static IServiceCollection AddXiaomiModelProvider(
        this IServiceCollection services,
        XiaomiModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        return services.AddXiaomiModelProvider();
    }
}
