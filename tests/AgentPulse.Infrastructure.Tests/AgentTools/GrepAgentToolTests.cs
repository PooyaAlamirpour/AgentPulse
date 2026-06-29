using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Workspaces;

namespace AgentPulse.Infrastructure.Tests.AgentTools;

public sealed class GrepAgentToolTests
{
    [Fact]
    public async Task Finds_text_with_path_line_number_and_case_control()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("src/a.cs", "Alpha\nbeta\nALPHA\n");
        var tool = CreateTool();

        var insensitive = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"pattern\":\"alpha\",\"include\":\"**/*.cs\"}");
        var sensitive = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"pattern\":\"Alpha\",\"caseSensitive\":true}");

        Assert.True(insensitive.Succeeded);
        Assert.Contains("src/a.cs:1: Alpha", insensitive.Output, StringComparison.Ordinal);
        Assert.Contains("src/a.cs:3: ALPHA", insensitive.Output, StringComparison.Ordinal);
        Assert.True(sensitive.Succeeded);
        Assert.Contains("src/a.cs:1: Alpha", sensitive.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA", sensitive.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_regex_binary_file_limit_and_outside_path_are_handled()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a.txt", "hit\nhit\n");
        workspace.WriteBytes("binary.bin", [0, 1, 2, 3]);
        var tool = new GrepAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxGrepResults = 1 });

        var invalid = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"[\"}");
        var limited = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"hit\",\"maxResults\":20}");
        var binary = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"\\u0000\"}");
        var outside = await ExecuteAsync(tool, workspace.Root, "{\"pattern\":\"x\",\"basePath\":\"..\"}");

        Assert.False(invalid.Succeeded);
        Assert.True(limited.Succeeded);
        Assert.Contains("a.txt:1", limited.Output, StringComparison.Ordinal);
        Assert.Contains("truncated", limited.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(binary.Succeeded);
        Assert.Equal("No matches found.", binary.Output);
        Assert.False(outside.Succeeded);
    }

    [Fact]
    public async Task Include_pattern_filters_files()
    {
        using var workspace = new TemporaryWorkspace();
        workspace.WriteText("a.cs", "needle");
        workspace.WriteText("a.md", "needle");
        var tool = CreateTool();

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"pattern\":\"needle\",\"include\":\"*.cs\"}");

        Assert.Contains("a.cs:1", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("a.md", result.Output, StringComparison.Ordinal);
    }

    private static GrepAgentTool CreateTool() => new(
        new WorkspacePathResolver(),
        new AgentToolOptions());

    private static async Task<AgentToolResult> ExecuteAsync(
        GrepAgentTool tool,
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
                $"agentpulse-grep-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WriteText(string relative, string content)
        {
            var path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void WriteBytes(string relative, byte[] content)
        {
            var path = System.IO.Path.Combine(Root, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
