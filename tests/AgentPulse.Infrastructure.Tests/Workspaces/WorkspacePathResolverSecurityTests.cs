using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Tests.AgentTools;
using AgentPulse.Infrastructure.Workspaces;

namespace AgentPulse.Infrastructure.Tests.Workspaces;

public sealed class WorkspacePathResolverSecurityTests
{
    [Fact]
    public async Task Read_glob_and_grep_do_not_follow_symbolic_link_outside_workspace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        using var outside = new TemporaryWorkspace();
        await File.WriteAllTextAsync(outside.Path("secret.txt"), "outside-secret");
        Directory.CreateSymbolicLink(workspace.Path("escape"), outside.Root);

        var resolver = new WorkspacePathResolver();
        var options = new AgentToolOptions();
        var read = await ExecuteAsync(new ReadAgentTool(resolver, options), workspace.Root,
            "{\"path\":\"escape/secret.txt\"}");
        var glob = await ExecuteAsync(new GlobAgentTool(resolver, options), workspace.Root,
            "{\"pattern\":\"**/*\"}");
        var grep = await ExecuteAsync(new GrepAgentTool(resolver, options), workspace.Root,
            "{\"pattern\":\"outside-secret\"}");

        Assert.False(read.Succeeded);
        Assert.Contains("outside", read.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.txt", glob.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outside-secret", grep.Output, StringComparison.Ordinal);
    }

    private static async Task<AgentToolResult> ExecuteAsync(IAgentTool tool, string root, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(
            document.RootElement,
            new AgentToolExecutionContext(root, new TestResourcePermissionAuthorizer()),
            CancellationToken.None);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agentpulse-security-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
