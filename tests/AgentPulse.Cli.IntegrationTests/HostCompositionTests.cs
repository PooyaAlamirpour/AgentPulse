using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Hosting;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class HostCompositionTests
{
    [Fact]
    public void Cli_identity_and_configuration_section_are_final()
    {
        Assert.Equal("agentpulse", typeof(AgentPulseHost).Assembly.GetName().Name);
        Assert.Equal("AgentPulse:Cli", CliOptions.SectionName);
    }

    [Fact]
    public async Task Host_resolves_configuration_options_logging_and_cli_services()
    {
        var console = new TestConsole();
        var builder = AgentPulseHost.CreateBuilder(console);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{CliOptions.SectionName}:ApplicationName"] = "agentpulse-test",
        });

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);

        try
        {
            Assert.NotNull(host.Services.GetRequiredService<IConfiguration>());
            Assert.Equal(
                "agentpulse-test",
                host.Services.GetRequiredService<IOptions<CliOptions>>().Value.ApplicationName);
            Assert.NotNull(host.Services.GetRequiredService<ILogger<CliApplication>>());
            Assert.NotNull(host.Services.GetRequiredService<IPromptInputReader>());
            Assert.NotNull(host.Services.GetRequiredService<IRunCommandHandler>());
            Assert.NotNull(host.Services.GetRequiredService<CliApplication>());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cancellation_token_is_forwarded_to_run_handler()
    {
        var console = new TestConsole();
        var recordingHandler = new RecordingRunCommandHandler();
        var builder = AgentPulseHost.CreateBuilder(console);
        builder.Services.AddSingleton<IRunCommandHandler>(recordingHandler);

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);

        try
        {
            using var cancellationSource = new CancellationTokenSource();
            var application = host.Services.GetRequiredService<CliApplication>();

            var exitCode = await application.RunAsync(
                ["run", "hello"],
                cancellationSource.Token);

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Equal(cancellationSource.Token, recordingHandler.ReceivedToken);
            Assert.Equal("hello", recordingHandler.ReceivedPrompt);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingRunCommandHandler : IRunCommandHandler
    {
        public string? ReceivedPrompt { get; private set; }

        public CancellationToken ReceivedToken { get; private set; }

        public Task<int> HandleAsync(string prompt, CancellationToken cancellationToken)
        {
            ReceivedPrompt = prompt;
            ReceivedToken = cancellationToken;
            return Task.FromResult(ExitCodes.Success);
        }
    }
}
