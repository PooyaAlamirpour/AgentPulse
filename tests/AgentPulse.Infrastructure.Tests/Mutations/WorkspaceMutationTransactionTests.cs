using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.Mutations;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class WorkspaceMutationTransactionTests
{
    [Fact]
    public async Task Failure_before_commit_changes_nothing_and_removes_created_directories()
    {
        using var workspace = new TemporaryWorkspace();
        var service = CreateService();
        var plan = await service.PlanWriteAsync(
            workspace.Root,
            "nested/file.txt",
            "content",
            null,
            CancellationToken.None);
        var context = new AgentToolExecutionContext(
            workspace.Root,
            new RecordingAuthorizer(allow: false));

        await Assert.ThrowsAsync<MutationPermissionException>(() =>
            service.AuthorizeAndCommitAsync(plan, context, CancellationToken.None));

        Assert.False(File.Exists(workspace.PathOf("nested/file.txt")));
        Assert.False(Directory.Exists(workspace.PathOf("nested")));
    }

    [Fact]
    public async Task Failure_during_second_file_commit_restores_first_file()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.PathOf("a.txt");
        var second = workspace.PathOf("b.txt");
        await File.WriteAllTextAsync(first, "a\n");
        await File.WriteAllTextAsync(second, "b\n");
        var firstBefore = await File.ReadAllBytesAsync(first);
        var secondBefore = await File.ReadAllBytesAsync(second);
        var fileSystem = new FaultingMutationFileSystem(failOnCommitOperation: 2);
        var options = CreateOptions();
        var service = CreateService(options, fileSystem);
        var parser = new ApplyPatchParser(options);
        var plan = await service.PlanPatchAsync(
            workspace.Root,
            parser.Parse(
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
                """),
            CancellationToken.None);
        var context = new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer());

        await Assert.ThrowsAsync<IOException>(() =>
            service.AuthorizeAndCommitAsync(plan, context, CancellationToken.None));

        Assert.Equal(firstBefore, await File.ReadAllBytesAsync(first));
        Assert.Equal(secondBefore, await File.ReadAllBytesAsync(second));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Rollback_removes_created_file_when_later_commit_fails()
    {
        using var workspace = new TemporaryWorkspace();
        var existing = workspace.PathOf("existing.txt");
        await File.WriteAllTextAsync(existing, "before\n");
        var before = await File.ReadAllBytesAsync(existing);
        var fileSystem = new FaultingMutationFileSystem(failOnCommitOperation: 2);
        var options = CreateOptions();
        var service = CreateService(options, fileSystem);
        var parser = new ApplyPatchParser(options);
        var plan = await service.PlanPatchAsync(
            workspace.Root,
            parser.Parse(
                """
                *** Begin Patch
                *** Add File: created.txt
                +created
                *** Update File: existing.txt
                @@
                -before
                +after
                *** End Patch
                """),
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None));

        Assert.False(File.Exists(workspace.PathOf("created.txt")));
        Assert.Equal(before, await File.ReadAllBytesAsync(existing));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Rollback_restores_deleted_file_when_later_commit_fails()
    {
        using var workspace = new TemporaryWorkspace();
        var deleted = workspace.PathOf("delete.txt");
        var updated = workspace.PathOf("update.txt");
        await File.WriteAllTextAsync(deleted, "deleted\n");
        await File.WriteAllTextAsync(updated, "before\n");
        var deletedBefore = await File.ReadAllBytesAsync(deleted);
        var updatedBefore = await File.ReadAllBytesAsync(updated);
        var fileSystem = new FaultingMutationFileSystem(failOnCommitOperation: 2);
        var options = CreateOptions();
        var service = CreateService(options, fileSystem);
        var plan = await service.PlanPatchAsync(
            workspace.Root,
            new ApplyPatchParser(options).Parse(
                """
                *** Begin Patch
                *** Delete File: delete.txt
                *** Update File: update.txt
                @@
                -before
                +after
                *** End Patch
                """),
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None));

        Assert.Equal(deletedBefore, await File.ReadAllBytesAsync(deleted));
        Assert.Equal(updatedBefore, await File.ReadAllBytesAsync(updated));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Rollback_restores_move_source_when_destination_install_fails()
    {
        using var workspace = new TemporaryWorkspace();
        var source = workspace.PathOf("source.txt");
        await File.WriteAllTextAsync(source, "source\n");
        var before = await File.ReadAllBytesAsync(source);
        var fileSystem = new FaultingMutationFileSystem(failOnCommitOperation: 2);
        var options = CreateOptions();
        var service = CreateService(options, fileSystem);
        var plan = await service.PlanPatchAsync(
            workspace.Root,
            new ApplyPatchParser(options).Parse(
                """
                *** Begin Patch
                *** Update File: source.txt
                *** Move to: destination.txt
                *** End Patch
                """),
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(workspace.PathOf("destination.txt")));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Concurrent_plans_do_not_lose_updates()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var service = CreateService();
        var first = await service.PlanEditAsync(
            workspace.Root,
            "file.txt",
            [new TextReplacement("before", "first", false)],
            false,
            CancellationToken.None);
        var second = await service.PlanEditAsync(
            workspace.Root,
            "file.txt",
            [new TextReplacement("before", "second", false)],
            false,
            CancellationToken.None);
        var context = new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer());

        var firstResult = await service.AuthorizeAndCommitAsync(first, context, CancellationToken.None);
        await Assert.ThrowsAsync<MutationValidationException>(() =>
            service.AuthorizeAndCommitAsync(second, context, CancellationToken.None));

        Assert.Equal("first", await File.ReadAllTextAsync(path));
        Assert.Equal(1, firstResult.Additions);
    }

    [Fact]
    public async Task Backup_cleanup_failure_rolls_back_files_even_after_an_earlier_backup_was_deleted()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.PathOf("a.txt");
        var second = workspace.PathOf("b.txt");
        await File.WriteAllTextAsync(first, "a\n");
        await File.WriteAllTextAsync(second, "b\n");
        var firstBefore = await File.ReadAllBytesAsync(first);
        var secondBefore = await File.ReadAllBytesAsync(second);
        var options = CreateOptions();
        var service = CreateService(
            options,
            new FailOnBackupDeleteMutationFileSystem(failOnBackupDelete: 2));
        var plan = await service.PlanPatchAsync(
            workspace.Root,
            new ApplyPatchParser(options).Parse(
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
                """),
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None));

        Assert.Equal(firstBefore, await File.ReadAllBytesAsync(first));
        Assert.Equal(secondBefore, await File.ReadAllBytesAsync(second));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Successful_commit_removes_all_temp_and_backup_files()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before\n");
        var service = CreateService();
        var plan = await service.PlanEditAsync(
            workspace.Root,
            "file.txt",
            [new TextReplacement("before", "after", false)],
            false,
            CancellationToken.None);

        await service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None);

        Assert.Equal("after\n", await File.ReadAllTextAsync(path));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Staging_failure_removes_partial_temp_file_and_new_parent_directories()
    {
        using var workspace = new TemporaryWorkspace();
        var service = CreateService(fileSystem: new FailDuringStagingFileSystem());
        var plan = await service.PlanWriteAsync(
            workspace.Root,
            "nested/file.txt",
            "content",
            null,
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None));

        Assert.False(File.Exists(workspace.PathOf("nested/file.txt")));
        Assert.False(Directory.Exists(workspace.PathOf("nested")));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Cancellation_during_staging_removes_partial_temp_file_and_new_parent_directories()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(fileSystem: new CancelDuringStagingFileSystem(cancellation));
        var plan = await service.PlanWriteAsync(
            workspace.Root,
            "nested/file.txt",
            "content",
            null,
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            cancellation.Token));

        Assert.False(File.Exists(workspace.PathOf("nested/file.txt")));
        Assert.False(Directory.Exists(workspace.PathOf("nested")));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Cancellation_during_second_commit_restores_first_file()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var first = workspace.PathOf("a.txt");
        var second = workspace.PathOf("b.txt");
        await File.WriteAllTextAsync(first, "a\n");
        await File.WriteAllTextAsync(second, "b\n");
        var firstBefore = await File.ReadAllBytesAsync(first);
        var secondBefore = await File.ReadAllBytesAsync(second);
        var options = CreateOptions();
        var service = CreateService(options, new CancelAfterFirstCommitFileSystem(cancellation));
        var plan = await service.PlanPatchAsync(
            workspace.Root,
            new ApplyPatchParser(options).Parse(
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
                """),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            cancellation.Token));

        Assert.Equal(firstBefore, await File.ReadAllBytesAsync(first));
        Assert.Equal(secondBefore, await File.ReadAllBytesAsync(second));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Pure_move_preserves_exact_bytes_including_mixed_line_endings()
    {
        using var workspace = new TemporaryWorkspace();
        var source = workspace.PathOf("source.txt");
        var bytes = System.Text.Encoding.UTF8.GetBytes("first\r\nsecond\nthird\r\n");
        await File.WriteAllBytesAsync(source, bytes);
        var options = CreateOptions();
        var service = CreateService(options);
        var plan = await service.PlanPatchAsync(
            workspace.Root,
            new ApplyPatchParser(options).Parse(
                """
                *** Begin Patch
                *** Update File: source.txt
                *** Move to: destination.txt
                *** End Patch
                """),
            CancellationToken.None);

        await service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.PathOf("destination.txt")));
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Utf16_big_endian_bom_is_preserved()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        var encoding = new System.Text.UnicodeEncoding(true, true, true);
        await File.WriteAllTextAsync(path, "before\n", encoding);
        var service = CreateService();
        var plan = await service.PlanEditAsync(
            workspace.Root,
            "file.txt",
            [new TextReplacement("before", "after", false)],
            false,
            CancellationToken.None);

        await service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }));
    }

    [Fact]
    public async Task Existing_unix_mode_is_preserved_when_supported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("script.sh");
        await File.WriteAllTextAsync(path, "before\n");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(path, mode);
        var service = CreateService();
        var plan = await service.PlanEditAsync(
            workspace.Root,
            "script.sh",
            [new TextReplacement("before", "after", false)],
            false,
            CancellationToken.None);

        await service.AuthorizeAndCommitAsync(
            plan,
            new AgentToolExecutionContext(workspace.Root, new RecordingAuthorizer()),
            CancellationToken.None);

        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

}
