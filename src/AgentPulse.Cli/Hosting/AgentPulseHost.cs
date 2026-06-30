using AgentPulse.Application.AgentLoop;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.Processes;
using AgentPulse.Application.Permissions;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Console;
using AgentPulse.Cli.Credentials;
using AgentPulse.Cli.Permissions;
using AgentPulse.Infrastructure;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Permissions;
using AgentPulse.Infrastructure.Processes;
using AgentPulse.Infrastructure.ProjectContexts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace AgentPulse.Cli.Hosting;

public static class AgentPulseHost
{
    public static HostApplicationBuilder CreateBuilder(
        IConsole? console = null,
        Action<IConfigurationBuilder>? configureConfiguration = null,
        string? contentRootPath = null,
        string? environmentName = null)
    {
        environmentName ??= Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            environmentName = Environments.Production;
        }

        contentRootPath = string.IsNullOrWhiteSpace(contentRootPath)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(contentRootPath);

        var settings = new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(AgentPulseHost).Assembly.GetName().Name,
            ContentRootPath = contentRootPath,
            EnvironmentName = environmentName,
            Args = Array.Empty<string>(),
            DisableDefaults = true,
        };

        var builder = Host.CreateApplicationBuilder(settings);
        builder.Configuration
            .SetBasePath(contentRootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(
                $"appsettings.{environmentName}.json",
                optional: true,
                reloadOnChange: false)
            .AddEnvironmentVariables();
        configureConfiguration?.Invoke(builder.Configuration);

        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.ColorBehavior = LoggerColorBehavior.Disabled;
        });
        builder.Services.Configure<ConsoleLoggerOptions>(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddOptions<CliOptions>()
            .Bind(builder.Configuration.GetSection(CliOptions.SectionName))
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.ApplicationName),
                $"{CliOptions.SectionName}:ApplicationName must not be empty.")
            .ValidateOnStart();

        builder.Services
            .AddOptions<OpenAiCompatibleModelOptions>()
            .Bind(builder.Configuration.GetSection(OpenAiCompatibleModelOptions.SectionName))
            .Validate(
                static options => IsValid(options),
                $"{OpenAiCompatibleModelOptions.SectionName} contains invalid model endpoint settings.")
            .ValidateOnStart();

        var sessionRunOptions = new SessionRunOptions();
        builder.Configuration.GetSection(SessionRunOptions.SectionName).Bind(sessionRunOptions);

        var streamingRunOptions = new StreamingRunOptions();
        builder.Configuration.GetSection(StreamingRunOptions.SectionName).Bind(streamingRunOptions);

        var agentToolOptions = new AgentToolOptions();
        builder.Configuration.GetSection(AgentToolOptions.SectionName).Bind(agentToolOptions);
        agentToolOptions.Validate();

        var mutationToolOptions = new MutationToolOptions();
        builder.Configuration.GetSection(MutationToolOptions.SectionName).Bind(mutationToolOptions);
        mutationToolOptions.Validate();

        var permissionOptions = new PermissionOptions();
        builder.Configuration.GetSection(PermissionOptions.SectionName).Bind(permissionOptions);
        permissionOptions.Validate();

        RunLeaseOptionsValidator.Validate(sessionRunOptions, streamingRunOptions);

        var modelOptions = new OpenAiCompatibleModelOptions();
        builder.Configuration.GetSection(OpenAiCompatibleModelOptions.SectionName).Bind(modelOptions);
        modelOptions.Validate();
        var credentialScope = modelOptions.CreateCredentialScope();

        var persistenceOptions = new PersistenceOptions();
        builder.Configuration.GetSection(PersistenceOptions.SectionName).Bind(persistenceOptions);
        var applicationDataPathProvider = new ApplicationDataPathProvider();
        var databasePath = applicationDataPathProvider.ResolveDatabasePath(
            persistenceOptions.DatabasePath);

        builder.Services.AddSingleton(sessionRunOptions);
        builder.Services.AddSingleton(streamingRunOptions);
        builder.Services.AddSingleton(agentToolOptions);
        builder.Services.AddSingleton(mutationToolOptions);
        builder.Services.AddSingleton(permissionOptions);
        builder.Services.AddSingleton(modelOptions);
        builder.Services.AddSingleton(new ChatModelRunDefaults(modelOptions.Model));
        builder.Services.AddSingleton(credentialScope);
        builder.Services.AddSingleton(persistenceOptions);
        builder.Services.AddSingleton<IApplicationDataPathProvider>(applicationDataPathProvider);
        builder.Services.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var configuredRoot = configuration[
                $"{ProviderCredentialStoreOptions.SectionName}:CredentialRootPath"];
            var rootPath = string.IsNullOrWhiteSpace(configuredRoot)
                ? ProviderCredentialStoreOptions.GetDefaultRootPath()
                : Path.GetFullPath(configuredRoot);
            return new ProviderCredentialStoreOptions(rootPath);
        });

        builder.Services.AddSingleton<IProjectFileSystem, SystemProjectFileSystem>();
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IPlatformProvider, SystemPlatformProvider>();
        builder.Services.AddSingleton<IProjectIdFactory, DeterministicProjectIdFactory>();
        builder.Services.AddSingleton<IProjectContextFactory, ProjectContextFactory>();
        builder.Services.AddSingleton<IAsyncDelay, SystemAsyncDelay>();

        builder.Services.AddAgentPulsePersistence(databasePath);
        builder.Services.AddAgentPulseCredentialStore();
        builder.Services.AddOpenAiCompatibleModelProvider();
        builder.Services.AddAgentTools();
        builder.Services.AddSingleton<IPermissionRuleEvaluator, PermissionRuleEvaluator>();
        builder.Services.AddSingleton<ISessionPermissionStore, InMemorySessionPermissionStore>();
        builder.Services.AddSingleton<IProjectPermissionStore>(
            new JsonProjectPermissionStore(
                Path.Combine(Path.GetDirectoryName(databasePath)!, "permissions")));
        builder.Services.AddSingleton<IPermissionApprovalPrompt, CliPermissionApprovalPrompt>();
        builder.Services.AddSingleton<IPermissionGate, PermissionGate>();

        builder.Services.AddScoped<IRegisterProject, RegisterProject>();
        builder.Services.AddScoped<ICreateSession, CreateSession>();
        builder.Services.AddScoped<IContinueSession, ContinueSession>();
        builder.Services.AddScoped<IPrepareSessionRun, PrepareSessionRun>();
        builder.Services.AddScoped<IEndSessionRun, EndSessionRun>();
        builder.Services.AddScoped<IRenewSessionRunLease, RenewSessionRunLease>();
        builder.Services.AddSingleton<IChatModelHistoryPolicy, ChatModelHistoryPolicy>();
        builder.Services.AddSingleton<IChatModelRequestBuilder, ChatModelRequestBuilder>();
        builder.Services.AddScoped<IAgentLoop, AgentLoop>();
        builder.Services.AddScoped<RunPrompt>();
        builder.Services.AddScoped<IRunPrompt, ToolCallingRunPrompt>();

        builder.Services.AddSingleton<IConsole>(console ?? new SystemConsole());
        builder.Services.AddSingleton<IModelOutputSink, ConsoleModelOutputSink>();
        builder.Services.AddSingleton<IRunCommandParser, RunCommandParser>();
        builder.Services.AddSingleton<IPromptInputReader, PromptInputReader>();
        builder.Services.AddSingleton<IEnvironmentVariableReader, SystemEnvironmentVariableReader>();
        builder.Services.AddSingleton<IConsoleKeyReader, SystemConsoleKeyReader>();
        builder.Services.AddSingleton<ISecretInputReader, SystemSecretInputReader>();
        builder.Services.AddSingleton<IProviderCredentialResolver, ProviderCredentialResolver>();
        builder.Services.AddSingleton<IAuthCommandHandler, AuthCommandHandler>();
        builder.Services.AddSingleton<ICliErrorRenderer, CliErrorRenderer>();
        builder.Services.AddSingleton<IRunCommandHandler, RunCommandHandler>();
        builder.Services.AddSingleton<CliApplication>();

        return builder;
    }

    private static bool IsValid(OpenAiCompatibleModelOptions options)
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
