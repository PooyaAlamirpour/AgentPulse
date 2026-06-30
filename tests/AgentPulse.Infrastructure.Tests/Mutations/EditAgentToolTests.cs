using System.Text;
using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.AgentTools;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class EditAgentToolTests
{
    [Fact]
    public async Task Replaces_one_exact_occurrence()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "one two three\n");
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"two","new_text":"changed"}""");

        Assert.True(result.Succeeded);
        Assert.Equal("one changed three\n", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
        Assert.Contains("-one two three", result.Output, StringComparison.Ordinal);
        Assert.Contains("+one changed three", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_match_is_deterministic_and_writes_nothing()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "unchanged");
        var before = await File.ReadAllBytesAsync(path);
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"missing","new_text":"new"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Ambiguous_match_requires_more_context()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "same same");
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"same","new_text":"new"}""");

        Assert.False(result.Succeeded);
        Assert.Contains("matched more than once", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("same same", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
    }

    [Fact]
    public async Task Replace_all_replaces_every_exact_occurrence()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "same same same");
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"same","new_text":"new","replace_all":true}""");

        Assert.True(result.Succeeded);
        Assert.Equal("new new new", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
    }

    [Theory]
    [InlineData("{\"path\":\"sample.txt\",\"new_text\":\"x\"}", "old_text")]
    [InlineData("{\"path\":\"sample.txt\",\"old_text\":\"x\"}", "new_text")]
    [InlineData("{\"path\":\"sample.txt\",\"old_text\":\"\",\"new_text\":\"x\"}", "must not be empty")]
    [InlineData("{\"path\":\"sample.txt\",\"old_text\":\"x\",\"new_text\":\"x\"}", "must be different")]
    public async Task Rejects_invalid_edit_contract(string json, string expected)
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "x");
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(tool, workspace.Root, json);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("x", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
    }

    [Fact]
    public async Task Preserves_utf16_bom_and_crlf()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "one\r\ntwo\r\n", new UnicodeEncoding(false, true, true));
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"two\n","new_text":"changed\n"}""");

        Assert.True(result.Succeeded);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }));
        Assert.Equal("one\r\nchanged\r\n", new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public async Task Stale_file_before_commit_is_rejected()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "before");
        var authorizer = new RecordingAuthorizer(onCall: _ => File.WriteAllText(path, "external"));
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"before","new_text":"after"}""",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Contains("changed after", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Permission_denial_leaves_file_byte_identical()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await File.ReadAllBytesAsync(path);
        var authorizer = new RecordingAuthorizer(allow: false);
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"before","new_text":"after"}""",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Contains("Proposed changes:", Assert.Single(authorizer.Calls).Description!, StringComparison.Ordinal);
    }


    [Fact]
    public async Task Rejects_missing_file_directory_and_protected_path()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(workspace.PathOf("folder"));
        Directory.CreateDirectory(workspace.PathOf(".git"));
        await File.WriteAllTextAsync(workspace.PathOf(".git/config"), "before");
        var tool = new EditAgentTool(CreateService());

        var missing = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"missing.txt","old_text":"x","new_text":"y"}""");
        var directory = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"folder","old_text":"x","new_text":"y"}""");
        var protectedResult = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":".git/config","old_text":"before","new_text":"after"}""");

        Assert.False(missing.Succeeded);
        Assert.False(directory.Succeeded);
        Assert.False(protectedResult.Succeeded);
        Assert.Contains("does not exist", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("directory", directory.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protected path", protectedResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("before", await File.ReadAllTextAsync(workspace.PathOf(".git/config")));
    }

    [Fact]
    public async Task Cancellation_during_permission_leaves_file_and_artifacts_unchanged()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await File.ReadAllBytesAsync(path);
        var authorizer = new RecordingAuthorizer(onCall: _ => cancellation.Cancel());
        var tool = new EditAgentTool(CreateService());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","old_text":"before","new_text":"after"}""",
            authorizer,
            cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Exact_edit_preserves_unrelated_mixed_line_endings()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("mixed.txt");
        var before = Encoding.UTF8.GetBytes("one\r\ntwo\nthree\r\n");
        await File.WriteAllBytesAsync(path, before);
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"mixed.txt","old_text":"two","new_text":"changed"}""");

        Assert.True(result.Succeeded);
        Assert.Equal(
            Encoding.UTF8.GetBytes("one\r\nchanged\nthree\r\n"),
            await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Binary_file_is_rejected_without_modification()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("binary.dat");
        var bytes = new byte[] { 1, 0, 2, 3 };
        await File.WriteAllBytesAsync(path, bytes);
        var tool = new EditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"binary.dat","old_text":"x","new_text":"y"}""");

        Assert.False(result.Succeeded);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Contains("recognized text file encodings", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
