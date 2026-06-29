using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Workspaces;

namespace AgentPulse.Infrastructure.Tests.AgentTools;

public sealed class GlobAgentToolTests
{
    [Fact]
    public async Task Finds_sorted_matches_and_ignores_build_directories()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("src/B.cs", "");
        workspace.Write("src/A.cs", "");
        workspace.Write("src/readme.md", "");
        workspace.Write("bin/Hidden.cs", "");
        workspace.Write("obj/Hidden.cs", "");
        workspace.Write(".git/Hidden.cs", "");
        var tool = CreateTool();

        var result = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"**/*.cs\"}");

        Assert.True(result.Succeeded);
        Assert.Equal("src/A.cs\nsrc/B.cs", result.Output);
    }

    [Fact]
    public async Task No_match_invalid_pattern_limit_and_outside_base_are_handled()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.Write("a.cs", "");
        workspace.Write("b.cs", "");
        var tool = new GlobAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxGlobResults = 1 });

        var none = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"**/*.json\"}");
        var invalid = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"[broken\"}");
        var limited = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"*.cs\",\"maxResults\":20}");
        var outside = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"*\",\"basePath\":\"..\"}");

        Assert.True(none.Succeeded);
        Assert.Equal("No files found.", none.Output);
        Assert.False(invalid.Succeeded);
        Assert.True(limited.Succeeded);
        Assert.Contains("truncated", limited.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(outside.Succeeded);
    }

    private static GlobAgentTool CreateTool() => new(
        new WorkspacePathResolver(),
        new AgentToolOptions());

    private static async Task<AgentToolResult> ExecuteAsync(
        GlobAgentTool tool,
        string root,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return await tool.ExecuteAsync(
            document.RootElement,
            new AgentToolExecutionContext(root),
            CancellationToken.None);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agentpulse-glob-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relative, string content)
        {
            var path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
