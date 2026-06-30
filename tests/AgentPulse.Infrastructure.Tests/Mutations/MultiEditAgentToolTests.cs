using AgentPulse.Infrastructure.AgentTools;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class MultiEditAgentToolTests
{
    [Fact]
    public async Task Applies_multiple_sequential_edits_with_one_permission_and_one_final_diff()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "alpha beta gamma");
        var authorizer = new RecordingAuthorizer();
        var tool = new MultiEditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """
            {"path":"sample.txt","edits":[
              {"old_text":"alpha","new_text":"first"},
              {"old_text":"first beta","new_text":"dependent"},
              {"old_text":"gamma","new_text":"last"}
            ]}
            """,
            authorizer);

        Assert.True(result.Succeeded);
        Assert.Equal("dependent last", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
        Assert.Single(authorizer.Calls);
        Assert.Equal(1, CountOccurrences(result.Output, "--- a/sample.txt"));
    }

    [Fact]
    public async Task Second_edit_failure_keeps_original_bytes()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "alpha beta");
        var before = await File.ReadAllBytesAsync(path);
        var tool = new MultiEditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """
            {"path":"sample.txt","edits":[
              {"old_text":"alpha","new_text":"first"},
              {"old_text":"missing","new_text":"second"}
            ]}
            """);

        Assert.False(result.Succeeded);
        Assert.Contains("operation 2 failed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Ambiguous_edit_fails_atomically()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "a b b");
        var tool = new MultiEditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """
            {"path":"sample.txt","edits":[
              {"old_text":"a","new_text":"x"},
              {"old_text":"b","new_text":"y"}
            ]}
            """);

        Assert.False(result.Succeeded);
        Assert.Equal("a b b", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
    }

    [Fact]
    public async Task Replace_all_is_supported_in_staged_sequence()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "a a");
        var tool = new MultiEditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","edits":[{"old_text":"a","new_text":"b","replace_all":true}]}""");

        Assert.True(result.Succeeded);
        Assert.Equal("b b", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
    }

    [Fact]
    public async Task Empty_edit_list_is_rejected()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "a");
        var tool = new MultiEditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","edits":[]}""");

        Assert.False(result.Succeeded);
        Assert.Contains("at least one edit", result.Error, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task Uses_one_final_file_replace_for_all_edits()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "one two three");
        var fileSystem = new CountingMutationFileSystem();
        var tool = new MultiEditAgentTool(CreateService(fileSystem: fileSystem));

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """
            {
              "path":"sample.txt",
              "edits":[
                {"old_text":"one","new_text":"first"},
                {"old_text":"two","new_text":"second"}
              ]
            }
            """);

        Assert.True(result.Succeeded);
        Assert.Equal(1, fileSystem.ReplaceCount);
        Assert.Equal(0, fileSystem.MoveCount);
        Assert.Equal("first second three", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Permission_denial_cancellation_stale_file_and_protected_path_are_atomic()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "before");
        var original = await File.ReadAllBytesAsync(path);
        var tool = new MultiEditAgentTool(CreateService());
        const string arguments = """
            {"path":"sample.txt","edits":[{"old_text":"before","new_text":"after"}]}
            """;

        var denied = await ExecuteAsync(
            tool,
            workspace.Root,
            arguments,
            new RecordingAuthorizer(allow: false));
        Assert.False(denied.Succeeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

        var staleAuthorizer = new RecordingAuthorizer(onCall: _ => File.WriteAllText(path, "external"));
        var stale = await ExecuteAsync(tool, workspace.Root, arguments, staleAuthorizer);
        Assert.False(stale.Succeeded);
        Assert.Equal("external", await File.ReadAllTextAsync(path));

        await File.WriteAllBytesAsync(path, original);
        using var cancellation = new CancellationTokenSource();
        var cancelAuthorizer = new RecordingAuthorizer(onCall: _ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            tool,
            workspace.Root,
            arguments,
            cancelAuthorizer,
            cancellation.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

        Directory.CreateDirectory(workspace.PathOf("obj"));
        await File.WriteAllTextAsync(workspace.PathOf("obj/protected.txt"), "before");
        var protectedResult = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"obj/protected.txt","edits":[{"old_text":"before","new_text":"after"}]}""");
        Assert.False(protectedResult.Succeeded);
        Assert.Equal("before", await File.ReadAllTextAsync(workspace.PathOf("obj/protected.txt")));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Missing_edit_fields_are_rejected_with_index()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "a");
        var tool = new MultiEditAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"sample.txt","edits":[{"old_text":"a"}]}""");

        Assert.False(result.Succeeded);
        Assert.Contains("operation 1", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new_text", result.Error, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }
}
