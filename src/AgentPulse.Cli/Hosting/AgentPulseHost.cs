using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Console;
using AgentPulse.Application.Processes;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Infrastructure;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Infrastructure.Processes;
using AgentPulse.Infrastructure.ProjectContexts;

namespace AgentPulse.Cli.Hosting;

public static class AgentPulseHost
{
    public static HostApplicationBuilder CreateBuilder(IConsole? console = null)
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

        builder.Logging.ClearProviders();
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

        builder.Services.AddSingleton<IProjectFileSystem, SystemProjectFileSystem>();
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IPlatformProvider, SystemPlatformProvider>();
        builder.Services.AddSingleton<IProjectIdFactory, DeterministicProjectIdFactory>();
        builder.Services.AddSingleton<IProjectContextFactory, ProjectContextFactory>();

        builder.Services.AddSingleton(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var options = new SessionRunOptions();
            configuration.GetSection(SessionRunOptions.SectionName).Bind(options);
            options.Validate();
            return options;
        });

        var configuredDatabasePath = builder.Configuration["AgentPulse:Persistence:DatabasePath"];
        var databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
            ? Path.Combine(AppContext.BaseDirectory, "agentpulse.db")
            : Path.GetFullPath(configuredDatabasePath, AppContext.BaseDirectory);
        builder.Services.AddAgentPulsePersistence(databasePath);

        builder.Services.AddScoped<IRegisterProject, RegisterProject>();
        builder.Services.AddScoped<ICreateSession, CreateSession>();
        builder.Services.AddScoped<IContinueSession, ContinueSession>();
        builder.Services.AddScoped<IPrepareSessionRun, PrepareSessionRun>();
        builder.Services.AddScoped<IEndSessionRun, EndSessionRun>();
        builder.Services.AddScoped<IRenewSessionRunLease, RenewSessionRunLease>();

        builder.Services.AddSingleton<IConsole>(console ?? new SystemConsole());
        builder.Services.AddSingleton<IPromptInputReader, PromptInputReader>();
        builder.Services.AddSingleton<IRunCommandHandler, RunCommandHandler>();
        builder.Services.AddSingleton<CliApplication>();

        return builder;
    }
}
