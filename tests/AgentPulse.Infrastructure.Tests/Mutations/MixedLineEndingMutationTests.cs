using System.Text;
using AgentPulse.Infrastructure.AgentTools;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class MixedLineEndingMutationTests
{
    [Fact]
    public async Task Apply_patch_preserves_unrelated_mixed_line_endings_byte_for_byte()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("alpha\r\nbeta\ngamma\r\n"));

        var result = await ApplyPatchAsync(
            workspace,
            "*** Begin Patch\n*** Update File: mixed.txt\n@@\n-gamma\n+delta\n*** End Patch");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("alpha\r\nbeta\ndelta\r\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Apply_patch_middle_line_update_preserves_every_other_terminator()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("one\r\ntwo\nthree\r\nfour\n"));

        var result = await ApplyPatchAsync(
            workspace,
            "*** Begin Patch\n*** Update File: mixed.txt\n@@\n-two\n+changed\n*** End Patch");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("one\r\nchanged\nthree\r\nfour\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Apply_patch_insert_only_hunk_uses_adjacent_terminator_without_normalizing_existing_lines()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("alpha\r\nbeta\ngamma\r\n"));

        var result = await ApplyPatchAsync(
            workspace,
            "*** Begin Patch\n*** Update File: mixed.txt\n@@\n+inserted\n*** End Patch");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("alpha\r\nbeta\ngamma\r\ninserted\r\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Apply_patch_delete_hunk_preserves_unrelated_terminators()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("alpha\r\nbeta\ngamma\r\n"));

        var result = await ApplyPatchAsync(
            workspace,
            "*** Begin Patch\n*** Update File: mixed.txt\n@@\n-beta\n*** End Patch");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("alpha\r\ngamma\r\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Apply_patch_multiple_hunks_leave_bytes_between_hunks_unchanged()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("alpha\r\nbeta\ngamma\r\ndelta\n"));

        var result = await ApplyPatchAsync(
            workspace,
            "*** Begin Patch\n*** Update File: mixed.txt\n@@\n-alpha\n+first\n@@\n-delta\n+last\n*** End Patch");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("first\r\nbeta\ngamma\r\nlast\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Edit_multiline_logical_newline_match_preserves_bytes_outside_the_match()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("prefix\r\none\r\ntwo\nthree\r\nsuffix\n"));
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"mixed.txt","old_text":"one\ntwo\nthree","new_text":"ONE\nTWO\nTHREE"}""");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("prefix\r\nONE\r\nTWO\nTHREE\r\nsuffix\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Multi_edit_preserves_unrelated_mixed_line_endings_and_commits_atomically()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("one\r\ntwo\nthree\r\nfour\n"));
        var tool = new MultiEditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """
            {"path":"mixed.txt","edits":[
              {"old_text":"one","new_text":"first"},
              {"old_text":"three","new_text":"third"}
            ]}
            """);

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("first\r\ntwo\nthird\r\nfour\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apply_patch_preserves_existing_trailing_newline_state(bool hasTrailingNewline)
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        var before = hasTrailingNewline
            ? "alpha\r\nbeta\ngamma\r\n"
            : "alpha\r\nbeta\ngamma";
        var expected = hasTrailingNewline
            ? "alpha\r\nbeta\ndelta\r\n"
            : "alpha\r\nbeta\ndelta";
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(before));

        var result = await ApplyPatchAsync(
            workspace,
            "*** Begin Patch\n*** Update File: mixed.txt\n@@\n-gamma\n+delta\n*** End Patch");

        Assert.True(result.Succeeded);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public async Task Apply_patch_preserves_encoding_bom_and_mixed_line_endings(string encodingName)
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        var encoding = CreateEncoding(encodingName);
        await File.WriteAllBytesAsync(path, Encode(encoding, "alpha\r\nbeta\ngamma\r\n"));

        var result = await ApplyPatchAsync(
            workspace,
            "*** Begin Patch\n*** Update File: mixed.txt\n@@\n-gamma\n+delta\n*** End Patch");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encode(encoding, "alpha\r\nbeta\ndelta\r\n"),
            await File.ReadAllBytesAsync(path));
    }

    private static Task<AgentPulse.Application.AgentTools.AgentToolResult> ApplyPatchAsync(
        TemporaryWorkspace workspace,
        string patch)
    {
        var arguments = System.Text.Json.JsonSerializer.Serialize(new { patch_text = patch });
        return ExecuteAsync(CreatePatchTool(), workspace.Root, arguments);
    }

    private static Encoding CreateEncoding(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8-bom" => new UTF8Encoding(true, true),
        "utf16-le" => new UnicodeEncoding(false, true, true),
        "utf16-be" => new UnicodeEncoding(true, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unsupported test encoding."),
    };

    private static byte[] Encode(Encoding encoding, string value)
    {
        var preamble = encoding.GetPreamble();
        var content = encoding.GetBytes(value);
        return [.. preamble, .. content];
    }
}
