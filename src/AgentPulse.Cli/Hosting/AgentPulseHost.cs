using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Console;

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

        builder.Services.AddSingleton<IConsole>(console ?? new SystemConsole());
        builder.Services.AddSingleton<IPromptInputReader, PromptInputReader>();
        builder.Services.AddSingleton<IRunCommandHandler, RunCommandHandler>();
        builder.Services.AddSingleton<CliApplication>();

        return builder;
    }
}
