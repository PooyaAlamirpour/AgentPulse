using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.Processes;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Console;
using AgentPulse.Cli.Credentials;
using AgentPulse.Infrastructure;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders;
using AgentPulse.Infrastructure.ModelProviders.Xiaomi;
using AgentPulse.Infrastructure.Persistence;
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
        Action<IConfigurationBuilder>? configureConfiguration = null)
    {
        var settings = new HostApplicationBuilderSettings
        {
            ApplicationName = typeof(AgentPulseHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            Args = Array.Empty<string>(),
        };

        var builder = Host.CreateApplicationBuilder(settings);

        builder.Configuration.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            optional: true,
            reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables();
        configureConfiguration?.Invoke(builder.Configuration);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
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

        var sessionRunOptions = new SessionRunOptions();
        builder.Configuration.GetSection(SessionRunOptions.SectionName).Bind(sessionRunOptions);

        var streamingRunOptions = new StreamingRunOptions();
        builder.Configuration.GetSection(StreamingRunOptions.SectionName).Bind(streamingRunOptions);

        RunLeaseOptionsValidator.Validate(sessionRunOptions, streamingRunOptions);

        var xiaomiModelOptions = new XiaomiModelOptions();
        builder.Configuration.GetSection(XiaomiModelOptions.SectionName).Bind(xiaomiModelOptions);
        xiaomiModelOptions.Validate();

        var persistenceOptions = new PersistenceOptions();
        builder.Configuration.GetSection(PersistenceOptions.SectionName).Bind(persistenceOptions);
        var applicationDataPathProvider = new ApplicationDataPathProvider();
        var databasePath = applicationDataPathProvider.ResolveDatabasePath(
            persistenceOptions.DatabasePath);

        builder.Services.AddSingleton(sessionRunOptions);
        builder.Services.AddSingleton(streamingRunOptions);
        builder.Services.AddSingleton(xiaomiModelOptions);
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
        builder.Services.AddXiaomiModelProvider();

        builder.Services.AddScoped<IRegisterProject, RegisterProject>();
        builder.Services.AddScoped<ICreateSession, CreateSession>();
        builder.Services.AddScoped<IContinueSession, ContinueSession>();
        builder.Services.AddScoped<IPrepareSessionRun, PrepareSessionRun>();
        builder.Services.AddScoped<IEndSessionRun, EndSessionRun>();
        builder.Services.AddScoped<IRenewSessionRunLease, RenewSessionRunLease>();
        builder.Services.AddSingleton<IChatModelHistoryPolicy, ChatModelHistoryPolicy>();
        builder.Services.AddSingleton<IChatModelRequestBuilder, ChatModelRequestBuilder>();
        builder.Services.AddScoped<IRunPrompt, RunPrompt>();

        builder.Services.AddSingleton<IConsole>(console ?? new SystemConsole());
        builder.Services.AddSingleton<IModelOutputSink, ConsoleModelOutputSink>();
        builder.Services.AddSingleton<IPromptInputReader, PromptInputReader>();
        builder.Services.AddSingleton<IEnvironmentVariableReader, SystemEnvironmentVariableReader>();
        builder.Services.AddSingleton<IConsoleKeyReader, SystemConsoleKeyReader>();
        builder.Services.AddSingleton<ISecretInputReader, SystemSecretInputReader>();
        builder.Services.AddSingleton<IProviderCredentialResolver, ProviderCredentialResolver>();
        builder.Services.AddSingleton<IAuthCommandHandler, AuthCommandHandler>();
        builder.Services.AddSingleton<IRunCommandHandler, RunCommandHandler>();
        builder.Services.AddSingleton<CliApplication>();

        return builder;
    }
}
