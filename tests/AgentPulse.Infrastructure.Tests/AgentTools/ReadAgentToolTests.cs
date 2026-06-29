using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Workspaces;

namespace AgentPulse.Infrastructure.Tests.AgentTools;

public sealed class ReadAgentToolTests
{
    [Fact]
    public async Task Reads_file_with_line_numbers_offset_and_limit()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.Path("sample.txt"), "one\ntwo\nthree\nfour\n");
        var tool = CreateTool();

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"path\":\"sample.txt\",\"offset\":2,\"limit\":2}");

        Assert.True(result.Succeeded);
        Assert.Contains("2: two", result.Output, StringComparison.Ordinal);
        Assert.Contains("3: three", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("1: one", result.Output, StringComparison.Ordinal);
        Assert.Contains("truncated", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_file_directory_and_outside_path_return_structured_failures()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(workspace.Path("folder"));
        var tool = CreateTool();

        var missing = await ExecuteAsync(tool, workspace.Root, "{\"path\":\"missing.txt\"}");
        var directory = await ExecuteAsync(tool, workspace.Root, "{\"path\":\"folder\"}");
        var outside = await ExecuteAsync(tool, workspace.Root, "{\"path\":\"../secret.txt\"}");

        Assert.False(missing.Succeeded);
        Assert.False(directory.Succeeded);
        Assert.False(outside.Succeeded);
        Assert.Contains("outside", outside.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Large_file_is_limited_without_loading_all_lines_into_output()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllLinesAsync(
            workspace.Path("large.txt"),
            Enumerable.Range(1, 100).Select(static value => $"line-{value}"));
        var tool = new ReadAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxReadLines = 5, MaxOutputCharacters = 1000 });

        var result = await ExecuteAsync(tool, workspace.Root, "{\"path\":\"large.txt\"}");

        Assert.True(result.Succeeded);
        Assert.Contains("1: line-1", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("6: line-6", result.Output, StringComparison.Ordinal);
        Assert.Contains("truncated", result.Output, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task Rejects_file_larger_than_max_readable_bytes_before_reading()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.Path("large-line.txt"), new string('x', 128));
        var tool = new ReadAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxReadableFileBytes = 32, MaxOutputCharacters = 1000 });

        var result = await ExecuteAsync(tool, workspace.Root, "{\"path\":\"large-line.txt\"}");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Output);
        Assert.Contains("exceeds", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("32 bytes", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reads_file_exactly_equal_to_max_readable_bytes()
    {
        using var workspace = new TemporaryWorkspace();
        var content = "éééé";
        await File.WriteAllTextAsync(workspace.Path("utf8.txt"), content, new System.Text.UTF8Encoding(false));
        var byteLength = new FileInfo(workspace.Path("utf8.txt")).Length;
        var tool = new ReadAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxReadableFileBytes = byteLength, MaxOutputCharacters = 1000 });

        var result = await ExecuteAsync(tool, workspace.Root, "{\"path\":\"utf8.txt\"}");

        Assert.True(result.Succeeded);
        Assert.Contains(content, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain('�', result.Output);
    }

    [Fact]
    public async Task Offset_and_limit_cannot_bypass_max_readable_bytes()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.Path("oversized.txt"), "skip\n" + new string('z', 100));
        var tool = new ReadAgentTool(
            new WorkspacePathResolver(),
            new AgentToolOptions { MaxReadableFileBytes = 16, MaxOutputCharacters = 1000 });

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            "{\"path\":\"oversized.txt\",\"offset\":2,\"limit\":1}");

        Assert.False(result.Succeeded);
        Assert.Contains("maximum readable size", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static ReadAgentTool CreateTool() => new(
        new WorkspacePathResolver(),
        new AgentToolOptions());

    private static async Task<AgentToolResult> ExecuteAsync(
        ReadAgentTool tool,
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
                $"agentpulse-read-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Path(string relative) => System.IO.Path.Combine(Root, relative);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
