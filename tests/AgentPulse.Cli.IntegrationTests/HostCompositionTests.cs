using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Hosting;
using AgentPulse.Application.Processes;
using AgentPulse.Application.ModelRequests;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ChatModels;
using AgentPulse.Application.Persistence;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Infrastructure.Persistence;
using AgentPulse.Infrastructure.Credentials;
using AgentPulse.Infrastructure.ModelProviders.Xiaomi;
using AgentPulse.Cli.Credentials;
using AgentPulse.Application.ProjectContexts;

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
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "agentpulse-host-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(tempRoot, "agentpulse.db");
        var credentialRootPath = Path.Combine(tempRoot, "security");
        var builder = AgentPulseHost.CreateBuilder(
            console,
            configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{CliOptions.SectionName}:ApplicationName"] = "agentpulse-test",
                ["AgentPulse:Persistence:DatabasePath"] = databasePath,
                [$"{ProviderCredentialStoreOptions.SectionName}:CredentialRootPath"] = credentialRootPath,
            }));

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);

        try
        {
            Assert.NotNull(host.Services.GetRequiredService<IConfiguration>());
            Assert.Equal(
                "agentpulse-test",
                host.Services.GetRequiredService<IOptions<CliOptions>>().Value.ApplicationName);
            Assert.NotNull(host.Services.GetRequiredService<ILogger<CliApplication>>());
            Assert.NotNull(host.Services.GetRequiredService<IProjectFileSystem>());
            Assert.NotNull(host.Services.GetRequiredService<IProcessRunner>());
            Assert.NotNull(host.Services.GetRequiredService<IGitService>());
            Assert.NotNull(host.Services.GetRequiredService<IClock>());
            Assert.NotNull(host.Services.GetRequiredService<IPlatformProvider>());
            Assert.NotNull(host.Services.GetRequiredService<IProjectIdFactory>());
            Assert.NotNull(host.Services.GetRequiredService<IProjectContextFactory>());
            Assert.NotNull(host.Services.GetRequiredService<SessionRunOptions>());
            Assert.NotNull(host.Services.GetRequiredService<StreamingRunOptions>());
            Assert.NotNull(host.Services.GetRequiredService<XiaomiModelOptions>());
            Assert.NotNull(host.Services.GetRequiredService<ProviderCredentialStoreOptions>());
            Assert.NotNull(host.Services.GetRequiredService<PersistenceOptions>());
            Assert.NotNull(host.Services.GetRequiredService<IApplicationDataPathProvider>());
            Assert.NotNull(host.Services.GetRequiredService<IStreamingRunPersistence>());
            Assert.NotNull(host.Services.GetRequiredService<IRunLeaseRenewalService>());

            using var scope = host.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<AgentPulseDbContext>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProjectRepository>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISessionRepository>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMessageRepository>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRunLeaseRepository>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRegisterProject>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICreateSession>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IContinueSession>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPrepareSessionRun>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEndSessionRun>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRenewSessionRunLease>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProviderCredentialSession>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChatModelClient>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRunPrompt>());
            Assert.NotNull(host.Services.GetRequiredService<IChatModelHistoryPolicy>());
            Assert.NotNull(host.Services.GetRequiredService<IChatModelRequestBuilder>());

            Assert.NotNull(host.Services.GetRequiredService<IPromptInputReader>());
            Assert.NotNull(host.Services.GetRequiredService<IProviderCredentialStore>());
            Assert.NotNull(host.Services.GetRequiredService<IProviderCredentialResolver>());
            Assert.NotNull(host.Services.GetRequiredService<IModelOutputSink>());
            Assert.NotNull(host.Services.GetRequiredService<IRunCommandHandler>());
            Assert.NotNull(host.Services.GetRequiredService<CliApplication>());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }


    [Fact]
    public void Host_rejects_unsafe_cross_lease_configuration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentPulseHost.CreateBuilder(
                new TestConsole(),
                configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{SessionRunOptions.SectionName}:LeaseDuration"] = "00:00:30",
                    [$"{StreamingRunOptions.SectionName}:LeaseRenewInterval"] = "00:00:20",
                    ["AgentPulse:Persistence:DatabasePath"] = Path.Combine(
                        Path.GetTempPath(),
                        "agentpulse-invalid-options",
                        "agentpulse.db"),
                })));

        Assert.Contains("half", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_token_is_forwarded_to_run_handler()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "agentpulse-host-cancellation-tests",
            Guid.NewGuid().ToString("N"));
        var console = new TestConsole();
        var recordingHandler = new RecordingRunCommandHandler();
        var builder = AgentPulseHost.CreateBuilder(
            console,
            configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentPulse:Persistence:DatabasePath"] = Path.Combine(tempRoot, "agentpulse.db"),
                [$"{ProviderCredentialStoreOptions.SectionName}:CredentialRootPath"] =
                    Path.Combine(tempRoot, "security"),
            }));
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
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
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
