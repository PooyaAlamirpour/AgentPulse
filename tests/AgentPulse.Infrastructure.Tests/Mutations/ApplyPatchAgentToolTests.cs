using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.AgentTools;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class ApplyPatchAgentToolTests
{
    [Fact]
    public async Task Adds_updates_deletes_and_moves_files_in_one_transaction()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("update.txt"), "old\nkeep\n");
        await File.WriteAllTextAsync(workspace.PathOf("delete.txt"), "obsolete\n");
        await File.WriteAllTextAsync(workspace.PathOf("move.txt"), "move old\n");
        var authorizer = new RecordingAuthorizer();
        var tool = CreatePatchTool();
        var patch =
            """
            *** Begin Patch
            *** Add File: new/file.txt
            +created
            *** Update File: update.txt
            @@
            -old
            +new
             keep
            *** Delete File: delete.txt
            *** Update File: move.txt
            *** Move to: moved/file.txt
            @@
            -move old
            +move new
            *** End Patch
            """;

        var result = await ExecutePatchAsync(tool, workspace.Root, patch, authorizer);

        Assert.True(result.Succeeded);
        Assert.Equal("created\n", await File.ReadAllTextAsync(workspace.PathOf("new/file.txt")));
        Assert.Equal("new\nkeep\n", await File.ReadAllTextAsync(workspace.PathOf("update.txt")));
        Assert.False(File.Exists(workspace.PathOf("delete.txt")));
        Assert.False(File.Exists(workspace.PathOf("move.txt")));
        Assert.Equal("move new\n", await File.ReadAllTextAsync(workspace.PathOf("moved/file.txt")));
        Assert.Equal(5, authorizer.Calls.Count);
        Assert.Contains(authorizer.Calls, call => call.Operation == "write" && call.Target == "new/file.txt");
        Assert.Contains(authorizer.Calls, call => call.Operation == "delete" && call.Target == "delete.txt");
        Assert.Equal(2, authorizer.Calls.Count(call => call.Operation == "move"));
    }

    [Fact]
    public async Task Supports_multiple_hunks_and_insert_only_hunk()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("sample.txt"), "one\ntwo\nthree\n");
        var tool = CreatePatchTool();
        var patch =
            """
            *** Begin Patch
            *** Update File: sample.txt
            @@
            -one
            +first
            @@
            -three
            +third
            @@
            +tail
            *** End Patch
            """;

        var result = await ExecutePatchAsync(tool, workspace.Root, patch);

        Assert.True(result.Succeeded);
        Assert.Equal("first\ntwo\nthird\ntail\n", await File.ReadAllTextAsync(workspace.PathOf("sample.txt")));
    }

    [Theory]
    [InlineData("", "must not be empty")]
    [InlineData("*** Add File: x.txt\n+x\n*** End Patch", "Begin Patch")]
    [InlineData("*** Begin Patch\n*** Add File: x.txt\n+x", "End Patch")]
    [InlineData("*** Begin Patch\n*** Unknown: x\n*** End Patch", "Unknown patch command")]
    [InlineData("*** Begin Patch\n*** Update File: x.txt\ninvalid\n*** End Patch", "Malformed update hunk")]
    [InlineData("*** Begin Patch\n*** Update File: x.txt\n@@\n context only\n*** End Patch", "does not contain a change")]
    [InlineData("*** Begin Patch\n*** Add File: /absolute.txt\n+x\n*** End Patch", "workspace-relative")]
    [InlineData("*** Begin Patch\n*** Add File: C:/absolute.txt\n+x\n*** End Patch", "workspace-relative")]
    [InlineData("*** Begin Patch\n*** Add File: ../outside.txt\n+x\n*** End Patch", "traversal")]
    public async Task Rejects_malformed_patches_without_workspace_changes(
        string patch,
        string expected)
    {
        using var workspace = new TemporaryWorkspace();
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(tool, workspace.Root, patch);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task Rejects_add_existing_update_missing_delete_missing_and_delete_directory()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("existing.txt"), "x");
        Directory.CreateDirectory(workspace.PathOf("folder"));
        var tool = CreatePatchTool();

        var addExisting = await ExecutePatchAsync(
            tool,
            workspace.Root,
            "*** Begin Patch\n*** Add File: existing.txt\n+x\n*** End Patch");
        var updateMissing = await ExecutePatchAsync(
            tool,
            workspace.Root,
            "*** Begin Patch\n*** Update File: missing.txt\n@@\n-x\n+y\n*** End Patch");
        var deleteMissing = await ExecutePatchAsync(
            tool,
            workspace.Root,
            "*** Begin Patch\n*** Delete File: missing.txt\n*** End Patch");
        var deleteDirectory = await ExecutePatchAsync(
            tool,
            workspace.Root,
            "*** Begin Patch\n*** Delete File: folder\n*** End Patch");

        Assert.All(new[] { addExisting, updateMissing, deleteMissing, deleteDirectory },
            result => Assert.False(result.Succeeded));
        Assert.Equal("x", await File.ReadAllTextAsync(workspace.PathOf("existing.txt")));
        Assert.True(Directory.Exists(workspace.PathOf("folder")));
    }

    [Fact]
    public async Task Rejects_move_to_existing_destination()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("source.txt"), "source\n");
        await File.WriteAllTextAsync(workspace.PathOf("destination.txt"), "destination\n");
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            """
            *** Begin Patch
            *** Update File: source.txt
            *** Move to: destination.txt
            *** End Patch
            """);

        Assert.False(result.Succeeded);
        Assert.Equal("source\n", await File.ReadAllTextAsync(workspace.PathOf("source.txt")));
        Assert.Equal("destination\n", await File.ReadAllTextAsync(workspace.PathOf("destination.txt")));
    }

    [Fact]
    public async Task Rejects_duplicate_conflicting_paths()
    {
        using var workspace = new TemporaryWorkspace();
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            """
            *** Begin Patch
            *** Add File: same.txt
            +one
            *** Add File: same.txt
            +two
            *** End Patch
            """);

        Assert.False(result.Succeeded);
        Assert.Contains("conflicting operations", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_denied_path_aborts_entire_patch_before_commit()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("a.txt"), "a\n");
        await File.WriteAllTextAsync(workspace.PathOf("b.txt"), "b\n");
        var authorizer = new SelectiveAuthorizer("b.txt");
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            """
            *** Begin Patch
            *** Update File: a.txt
            @@
            -a
            +changed-a
            *** Update File: b.txt
            @@
            -b
            +changed-b
            *** End Patch
            """,
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal("a\n", await File.ReadAllTextAsync(workspace.PathOf("a.txt")));
        Assert.Equal("b\n", await File.ReadAllTextAsync(workspace.PathOf("b.txt")));
    }

    [Fact]
    public async Task Stale_file_aborts_entire_patch()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("a.txt"), "a\n");
        await File.WriteAllTextAsync(workspace.PathOf("b.txt"), "b\n");
        var authorizer = new RecordingAuthorizer(onCall: call =>
        {
            if (call.Target == "b.txt")
            {
                File.WriteAllText(workspace.PathOf("a.txt"), "external\n");
            }
        });
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            """
            *** Begin Patch
            *** Update File: a.txt
            @@
            -a
            +changed-a
            *** Update File: b.txt
            @@
            -b
            +changed-b
            *** End Patch
            """,
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal("external\n", await File.ReadAllTextAsync(workspace.PathOf("a.txt")));
        Assert.Equal("b\n", await File.ReadAllTextAsync(workspace.PathOf("b.txt")));
    }


    [Fact]
    public async Task Rejects_patch_larger_than_configured_limit()
    {
        using var workspace = new TemporaryWorkspace();
        var options = CreateOptions();
        options.MaxPatchBytes = 32;
        var tool = CreatePatchTool(options);

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            "*** Begin Patch\n*** Add File: long.txt\n+long content\n*** End Patch");

        Assert.False(result.Succeeded);
        Assert.Contains("maximum size", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Theory]
    [InlineData("missing", "did not match")]
    [InlineData("same", "matched more than once")]
    public async Task Exact_hunk_matching_rejects_missing_or_ambiguous_context(
        string oldLine,
        string expected)
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("sample.txt");
        await File.WriteAllTextAsync(path, "same\nsame\n");
        var before = await File.ReadAllBytesAsync(path);
        var tool = CreatePatchTool();
        var patch = $"*** Begin Patch\n*** Update File: sample.txt\n@@\n-{oldLine}\n+changed\n*** End Patch";

        var result = await ExecutePatchAsync(tool, workspace.Root, patch);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Protected_move_destination_aborts_before_permission()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("source.txt"), "source\n");
        var authorizer = new RecordingAuthorizer();
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            """
            *** Begin Patch
            *** Update File: source.txt
            *** Move to: .git/source.txt
            *** End Patch
            """,
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Empty(authorizer.Calls);
        Assert.Equal("source\n", await File.ReadAllTextAsync(workspace.PathOf("source.txt")));
    }

    [Fact]
    public async Task Commit_failure_returns_transient_failure_and_rolls_back_every_file()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.PathOf("a.txt");
        var second = workspace.PathOf("b.txt");
        await File.WriteAllTextAsync(first, "a\n");
        await File.WriteAllTextAsync(second, "b\n");
        var firstBefore = await File.ReadAllBytesAsync(first);
        var secondBefore = await File.ReadAllBytesAsync(second);
        var tool = CreatePatchTool(fileSystem: new FaultingMutationFileSystem(2));

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            """
            *** Begin Patch
            *** Update File: a.txt
            @@
            -a
            +changed-a
            *** Update File: b.txt
            @@
            -b
            +changed-b
            *** End Patch
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Transient, result.FailureClassification);
        Assert.Equal(firstBefore, await File.ReadAllBytesAsync(first));
        Assert.Equal(secondBefore, await File.ReadAllBytesAsync(second));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Successful_result_contains_deterministic_multi_file_metadata()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(workspace.PathOf("existing.txt"), "before\n");
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            """
            *** Begin Patch
            *** Add File: created.txt
            +created
            *** Update File: existing.txt
            @@
            -before
            +after
            *** End Patch
            """);

        Assert.True(result.Succeeded);
        Assert.Equal("apply_patch", result.Metadata["operation"]);
        Assert.Equal("[\"created.txt\",\"existing.txt\"]", result.Metadata["paths"]);
        Assert.Contains("+++ b/created.txt", result.Output, StringComparison.Ordinal);
        Assert.Contains("+++ b/existing.txt", result.Output, StringComparison.Ordinal);
        Assert.True(int.Parse(result.Metadata["additions"], System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task Protected_path_aborts_before_permission()
    {
        using var workspace = new TemporaryWorkspace();
        var authorizer = new RecordingAuthorizer();
        var tool = CreatePatchTool();

        var result = await ExecutePatchAsync(
            tool,
            workspace.Root,
            "*** Begin Patch\n*** Add File: .git/config\n+x\n*** End Patch",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Empty(authorizer.Calls);
        Assert.Contains("protected path", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<AgentToolResult> ExecutePatchAsync(
        ApplyPatchAgentTool tool,
        string workspaceRoot,
        string patch,
        IAgentToolResourcePermissionAuthorizer? authorizer = null)
    {
        var json = JsonSerializer.Serialize(new { patch_text = patch });
        return ExecuteAsync(tool, workspaceRoot, json, authorizer);
    }

    private sealed class SelectiveAuthorizer(string deniedTarget)
        : IAgentToolResourcePermissionAuthorizer
    {
        public Task<PermissionAuthorizationResult> AuthorizeAsync(
            string operation,
            string target,
            string? description,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(target == deniedTarget
                ? PermissionAuthorizationResult.Reject(
                    AgentToolResult.Failure(
                        "Permission denied for mutation test.",
                        classification: AgentToolFailureClassification.Deterministic),
                    status: PermissionAuthorizationStatus.ApprovalUnavailable)
                : PermissionAuthorizationResult.Allow());
        }
    }
}
