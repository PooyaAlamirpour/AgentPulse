using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.Workspaces;
using AgentPulse.Application.Persistence;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;
using AgentPulse.Infrastructure.Mutations;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Persistence.Repositories;
using AgentPulse.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<IAgentToolTurnPersistence, AgentToolTurnPersistence>();
        services.AddSingleton<IRunLeaseRenewalService, RunLeaseRenewalService>();

        return services;
    }

    public static IServiceCollection AddAgentPulseCredentialStore(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(ProviderCredentialScope.Default);
        services.AddSingleton<DataProtectionProviderCredentialStore>();
        services.AddSingleton<IProviderCredentialStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DataProtectionProviderCredentialStore>());
        services.AddSingleton<ILegacyProviderCredentialStore>(serviceProvider =>
            serviceProvider.GetRequiredService<DataProtectionProviderCredentialStore>());
        services.AddScoped<IProviderCredentialSession>(serviceProvider =>
            new ProviderCredentialSession(
                serviceProvider.GetRequiredService<IProviderCredentialStore>(),
                serviceProvider.GetRequiredService<ILegacyProviderCredentialStore>(),
                serviceProvider.GetRequiredService<ProviderCredentialScope>()));
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

    public static IServiceCollection AddOpenAiCompatibleModelProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OpenAiCompatibleSseParser>();
        services.AddHttpClient(OpenAiCompatibleChatModelClient.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            })
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
        services.AddScoped<IChatModelClient, OpenAiCompatibleChatModelClient>();
        return services;
    }

    public static IServiceCollection AddOpenAiCompatibleModelProvider(
        this IServiceCollection services,
        OpenAiCompatibleModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(options.CreateCredentialScope());
        return services.AddOpenAiCompatibleModelProvider();
    }

    public static IServiceCollection AddAgentTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new MutationToolOptions());
        services.AddSingleton<IWorkspacePathResolver, WorkspacePathResolver>();
        services.AddSingleton<IProtectedPathPolicy>(serviceProvider =>
            new ProtectedPathPolicy(
                serviceProvider.GetRequiredService<IWorkspacePathResolver>(),
                serviceProvider.GetRequiredService<MutationToolOptions>(),
                serviceProvider.GetRequiredService<ILogger<ProtectedPathPolicy>>()));
        services.AddSingleton<IUnifiedDiffGenerator>(serviceProvider =>
            new UnifiedDiffGenerator(
                serviceProvider.GetRequiredService<MutationToolOptions>()));
        services.AddSingleton<IPathLockManager>(_ => new PathLockManager());
        services.AddSingleton<IMutationFileSystem>(_ => new SystemMutationFileSystem());
        services.AddSingleton<IApplyPatchParser>(serviceProvider =>
            new ApplyPatchParser(
                serviceProvider.GetRequiredService<MutationToolOptions>()));
        services.AddSingleton<IWorkspaceMutationService>(serviceProvider =>
            new WorkspaceMutationService(
                serviceProvider.GetRequiredService<IProtectedPathPolicy>(),
                serviceProvider.GetRequiredService<IUnifiedDiffGenerator>(),
                serviceProvider.GetRequiredService<IPathLockManager>(),
                serviceProvider.GetRequiredService<IMutationFileSystem>(),
                serviceProvider.GetRequiredService<MutationToolOptions>(),
                serviceProvider.GetRequiredService<ILogger<WorkspaceMutationService>>()));
        services.AddSingleton<IAgentTool, ReadAgentTool>();
        services.AddSingleton<IAgentTool, GlobAgentTool>();
        services.AddSingleton<IAgentTool, GrepAgentTool>();
        services.AddSingleton<IAgentTool>(serviceProvider =>
            new WriteAgentTool(
                serviceProvider.GetRequiredService<IWorkspaceMutationService>()));
        services.AddSingleton<IAgentTool>(serviceProvider =>
            new EditAgentTool(
                serviceProvider.GetRequiredService<IWorkspaceMutationService>()));
        services.AddSingleton<IAgentTool>(serviceProvider =>
            new MultiEditAgentTool(
                serviceProvider.GetRequiredService<IWorkspaceMutationService>()));
        services.AddSingleton<IAgentTool>(serviceProvider =>
            new ApplyPatchAgentTool(
                serviceProvider.GetRequiredService<IApplyPatchParser>(),
                serviceProvider.GetRequiredService<IWorkspaceMutationService>()));
        services.AddSingleton<IAgentToolRegistry>(serviceProvider =>
            new AgentToolRegistry(
                serviceProvider.GetServices<IAgentTool>(),
                requirePermissionMetadata: true));
        return services;
    }

}
