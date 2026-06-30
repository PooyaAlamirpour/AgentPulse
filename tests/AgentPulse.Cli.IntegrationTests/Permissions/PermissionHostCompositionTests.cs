using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Cli.Console;
using AgentPulse.Cli.Hosting;
using AgentPulse.Infrastructure.Credentials;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPulse.Cli.IntegrationTests.Permissions;

public sealed class PermissionHostCompositionTests
{
    [Fact]
    public void Missing_permission_section_preserves_allow_default()
    {
        using var directory = new TemporaryDirectory();
        var builder = AgentPulseHost.CreateBuilder(
            new TestConsole(),
            configuration => AddStorageConfiguration(configuration, directory.Path));

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<PermissionOptions>();

        Assert.Equal(PermissionDecision.Allow, options.GetDefaultDecision());
        Assert.Empty(options.CreateRules());
        Assert.NotNull(host.Services.GetRequiredService<IPermissionGate>());
        Assert.NotNull(host.Services.GetRequiredService<IPermissionApprovalPrompt>());
        Assert.NotNull(host.Services.GetRequiredService<ISessionPermissionStore>());
        Assert.NotNull(host.Services.GetRequiredService<IProjectPermissionStore>());
        var registry = host.Services.GetRequiredService<IAgentToolRegistry>();
        Assert.Equal(["glob", "grep", "read"], registry.GetDefinitions().Select(static tool => tool.Name));
    }

    [Fact]
    public void Permission_rules_bind_from_configuration()
    {
        using var directory = new TemporaryDirectory();
        var builder = AgentPulseHost.CreateBuilder(
            new TestConsole(),
            configuration =>
            {
                AddStorageConfiguration(configuration, directory.Path);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{PermissionOptions.SectionName}:DefaultDecision"] = "Allow",
                    [$"{PermissionOptions.SectionName}:Rules:0:Tool"] = "read",
                    [$"{PermissionOptions.SectionName}:Rules:0:Operation"] = "read",
                    [$"{PermissionOptions.SectionName}:Rules:0:Target"] = "secrets/**",
                    [$"{PermissionOptions.SectionName}:Rules:0:Decision"] = "Deny",
                    [$"{PermissionOptions.SectionName}:Rules:0:Scope"] = "Project",
                });
            });

        using var host = builder.Build();
        var rule = Assert.Single(host.Services
            .GetRequiredService<PermissionOptions>()
            .CreateRules());

        Assert.Equal("read", rule.Tool);
        Assert.Equal("secrets/**", rule.Target);
        Assert.Equal(PermissionDecision.Deny, rule.Decision);
    }

    [Fact]
    public void Invalid_permission_configuration_is_rejected_with_clear_message()
    {
        using var directory = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentPulseHost.CreateBuilder(
                new TestConsole(),
                configuration =>
                {
                    AddStorageConfiguration(configuration, directory.Path);
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"{PermissionOptions.SectionName}:DefaultDecision"] = "invalid",
                    });
                }));

        Assert.Contains("DefaultDecision", exception.Message, StringComparison.Ordinal);
    }

    private static void AddStorageConfiguration(
        IConfigurationBuilder configuration,
        string root)
    {
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentPulse:Persistence:DatabasePath"] = Path.Combine(root, "agentpulse.db"),
            [$"{ProviderCredentialStoreOptions.SectionName}:CredentialRootPath"] =
                Path.Combine(root, "credentials"),
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agentpulse-permission-host",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
