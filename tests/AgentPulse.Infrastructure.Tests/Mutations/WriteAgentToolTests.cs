using System.Text;
using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.AgentTools;
using AgentPulse.Infrastructure.Mutations;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class WriteAgentToolTests
{
    public static TheoryData<string> InvalidExpectedHashes => new()
    {
        "0",
        new string('0', 65),
        new string('g', 64),
        $"sha256:{new string('0', 64)}",
    };

    [Fact]
    public void Schema_keeps_expected_hash_optional_and_documents_existing_file_requirement()
    {
        var tool = new WriteAgentTool(CreateService());
        using var schema = JsonDocument.Parse(tool.ParametersJsonSchema);
        var root = schema.RootElement;

        var required = root.GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .ToArray();
        var description = root.GetProperty("properties")
            .GetProperty("expected_sha256")
            .GetProperty("description")
            .GetString();

        Assert.DoesNotContain("expected_sha256", required);
        Assert.Contains("Required when overwriting an existing file", description!, StringComparison.Ordinal);
        Assert.Contains("exact current bytes", description!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creates_new_file_and_nested_parent_directories()
    {
        using var workspace = new TemporaryWorkspace();
        var tool = new WriteAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"src/nested/New.cs","content":"content\n"}""");

        Assert.True(result.Succeeded);
        Assert.Equal("content\n", await File.ReadAllTextAsync(workspace.PathOf("src/nested/New.cs")));
        Assert.Contains("+++ b/src/nested/New.cs", result.Output, StringComparison.Ordinal);
        Assert.Equal(64, result.Metadata["sha256After"].Length);
    }

    [Fact]
    public async Task Writes_empty_new_file_as_utf8_without_bom()
    {
        using var workspace = new TemporaryWorkspace();
        var tool = new WriteAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"empty.txt","content":""}""");

        Assert.True(result.Succeeded);
        Assert.Empty(await File.ReadAllBytesAsync(workspace.PathOf("empty.txt")));
    }

    [Theory]
    [InlineData("{}", "non-empty path")]
    [InlineData("{\"path\":\"\",\"content\":\"x\"}", "non-empty path")]
    [InlineData("{\"path\":\"x.txt\"}", "requires content")]
    public async Task Rejects_invalid_required_arguments(string json, string expected)
    {
        using var workspace = new TemporaryWorkspace();
        var tool = new WriteAgentTool(CreateService());

        var result = await ExecuteAsync(tool, workspace.Root, json);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_directory_path()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(workspace.PathOf("folder"));
        var tool = new WriteAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"folder","content":"x"}""");

        Assert.False(result.Succeeded);
        Assert.Contains("directory", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_file_without_expected_hash_is_rejected_before_permission()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await File.ReadAllBytesAsync(path);
        var options = CreateOptions();
        var fileSystem = new CountingMutationFileSystem();
        var diffGenerator = new RecordingUnifiedDiffGenerator(options);
        var authorizer = new RecordingAuthorizer();
        var tool = new WriteAgentTool(CreateService(options, fileSystem, diffGenerator));

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"file.txt","content":"after"}""",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Contains("expected SHA-256", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Metadata);
        Assert.Empty(authorizer.Calls);
        Assert.Equal(0, diffGenerator.CallCount);
        Assert.Equal(0, fileSystem.StagedWriteCount);
        Assert.Equal(0, fileSystem.MoveCount);
        Assert.Equal(0, fileSystem.ReplaceCount);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Existing_file_rejects_null_or_blank_expected_hash_before_permission(
        string? expectedSha256)
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await File.ReadAllBytesAsync(path);
        var options = CreateOptions();
        var fileSystem = new CountingMutationFileSystem();
        var diffGenerator = new RecordingUnifiedDiffGenerator(options);
        var authorizer = new RecordingAuthorizer();
        var tool = new WriteAgentTool(CreateService(options, fileSystem, diffGenerator));
        var json = JsonSerializer.Serialize(new
        {
            path = "file.txt",
            content = "after",
            expected_sha256 = expectedSha256,
        });

        var result = await ExecuteAsync(tool, workspace.Root, json, authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Contains("expected SHA-256", result.Error, StringComparison.Ordinal);
        Assert.Empty(authorizer.Calls);
        Assert.Equal(0, diffGenerator.CallCount);
        Assert.Equal(0, fileSystem.StagedWriteCount);
        Assert.Equal(0, fileSystem.MoveCount);
        Assert.Equal(0, fileSystem.ReplaceCount);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Overwrites_existing_file_with_matching_hash_and_preserves_utf8_bom_and_crlf()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before\r\nline\r\n", new UTF8Encoding(true));
        var hash = TextFileCodec.ComputeSha256(await File.ReadAllBytesAsync(path));
        var options = CreateOptions();
        var diffGenerator = new RecordingUnifiedDiffGenerator(options);
        var authorizer = new RecordingAuthorizer();
        var tool = new WriteAgentTool(CreateService(options, diffGenerator: diffGenerator));
        var json = JsonSerializer.Serialize(new
        {
            path = "file.txt",
            content = "after\nline\n",
            expected_sha256 = hash,
        });

        var result = await ExecuteAsync(tool, workspace.Root, json, authorizer);

        Assert.True(result.Succeeded);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("after\r\nline\r\n", new UTF8Encoding(true).GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(TextFileCodec.ComputeSha256(bytes), result.Metadata["sha256After"]);
        Assert.Single(authorizer.Calls);
        Assert.True(diffGenerator.CallCount > 0);
    }

    [Fact]
    public async Task Accepts_uppercase_expected_hash_for_existing_file()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var hash = TextFileCodec.ComputeSha256(await File.ReadAllBytesAsync(path)).ToUpperInvariant();
        var authorizer = new RecordingAuthorizer();
        var tool = new WriteAgentTool(CreateService());
        var json = JsonSerializer.Serialize(new
        {
            path = "file.txt",
            content = "after",
            expected_sha256 = hash,
        });

        var result = await ExecuteAsync(tool, workspace.Root, json, authorizer);

        Assert.True(result.Succeeded);
        Assert.Equal("after", await File.ReadAllTextAsync(path));
        Assert.Single(authorizer.Calls);
    }

    [Fact]
    public async Task Rejects_wrong_valid_format_hash_before_permission_without_changing_file()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var original = await File.ReadAllBytesAsync(path);
        var options = CreateOptions();
        var fileSystem = new CountingMutationFileSystem();
        var diffGenerator = new RecordingUnifiedDiffGenerator(options);
        var authorizer = new RecordingAuthorizer();
        var tool = new WriteAgentTool(CreateService(options, fileSystem, diffGenerator));

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"file.txt","content":"after","expected_sha256":"0000000000000000000000000000000000000000000000000000000000000000"}""",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Contains("does not match", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Metadata);
        Assert.Empty(authorizer.Calls);
        Assert.Equal(0, diffGenerator.CallCount);
        Assert.Equal(0, fileSystem.StagedWriteCount);
        Assert.Equal(0, fileSystem.MoveCount);
        Assert.Equal(0, fileSystem.ReplaceCount);
        AssertNoMutationArtifacts(workspace.Root);
    }


    [Fact]
    public async Task Rejects_result_larger_than_configured_file_limit()
    {
        using var workspace = new TemporaryWorkspace();
        var options = CreateOptions();
        options.MaxFileBytes = 3;
        var tool = new WriteAgentTool(CreateService(options));

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"large.txt","content":"four"}""");

        Assert.False(result.Succeeded);
        Assert.Contains("maximum mutation size", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(workspace.PathOf("large.txt")));
    }

    [Theory]
    [MemberData(nameof(InvalidExpectedHashes))]
    public async Task Rejects_invalid_expected_hash_format_before_permission(
        string expectedSha256)
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await File.ReadAllBytesAsync(path);
        var options = CreateOptions();
        var fileSystem = new CountingMutationFileSystem();
        var diffGenerator = new RecordingUnifiedDiffGenerator(options);
        var authorizer = new RecordingAuthorizer();
        var tool = new WriteAgentTool(CreateService(options, fileSystem, diffGenerator));
        var json = JsonSerializer.Serialize(new
        {
            path = "file.txt",
            content = "after",
            expected_sha256 = expectedSha256,
        });

        var result = await ExecuteAsync(tool, workspace.Root, json, authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, result.FailureClassification);
        Assert.Contains("64 hexadecimal", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Empty(authorizer.Calls);
        Assert.Equal(0, diffGenerator.CallCount);
        Assert.Equal(0, fileSystem.StagedWriteCount);
        Assert.Equal(0, fileSystem.MoveCount);
        Assert.Equal(0, fileSystem.ReplaceCount);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task File_change_after_plan_aborts_overwrite_before_commit()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var hash = TextFileCodec.ComputeSha256(await File.ReadAllBytesAsync(path));
        var authorizer = new RecordingAuthorizer(onCall: _ => File.WriteAllText(path, "external"));
        var tool = new WriteAgentTool(CreateService());
        var json = JsonSerializer.Serialize(new
        {
            path = "file.txt",
            content = "after",
            expected_sha256 = hash,
        });

        var result = await ExecuteAsync(tool, workspace.Root, json, authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal("external", await File.ReadAllTextAsync(path));
        Assert.Contains("changed after", result.Error, StringComparison.OrdinalIgnoreCase);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Permission_deny_changes_nothing_and_diff_is_available_before_authorization()
    {
        using var workspace = new TemporaryWorkspace();
        var authorizer = new RecordingAuthorizer(allow: false);
        var tool = new WriteAgentTool(CreateService());

        var result = await ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"denied.txt","content":"secret"}""",
            authorizer);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(workspace.PathOf("denied.txt")));
        var call = Assert.Single(authorizer.Calls);
        Assert.Equal("write", call.Operation);
        Assert.Equal("denied.txt", call.Target);
        Assert.Contains("+++ b/denied.txt", call.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permission_deny_after_correct_hash_keeps_existing_file_unchanged()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.PathOf("file.txt");
        await File.WriteAllTextAsync(path, "before");
        var before = await File.ReadAllBytesAsync(path);
        var hash = TextFileCodec.ComputeSha256(before);
        var authorizer = new RecordingAuthorizer(allow: false);
        var tool = new WriteAgentTool(CreateService());
        var json = JsonSerializer.Serialize(new
        {
            path = "file.txt",
            content = "after",
            expected_sha256 = hash,
        });

        var result = await ExecuteAsync(tool, workspace.Root, json, authorizer);

        Assert.False(result.Succeeded);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var call = Assert.Single(authorizer.Calls);
        Assert.Equal("write", call.Operation);
        Assert.Equal("file.txt", call.Target);
        Assert.Contains("--- a/file.txt", call.Description!, StringComparison.Ordinal);
        AssertNoMutationArtifacts(workspace.Root);
    }

    [Fact]
    public async Task Cancellation_during_authorization_changes_nothing()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var authorizer = new RecordingAuthorizer(onCall: _ => cancellation.Cancel());
        var tool = new WriteAgentTool(CreateService());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(
            tool,
            workspace.Root,
            """{"path":"cancelled.txt","content":"x"}""",
            authorizer,
            cancellation.Token));

        Assert.False(File.Exists(workspace.PathOf("cancelled.txt")));
        Assert.Empty(Directory.EnumerateFiles(workspace.Root, ".agentpulse-*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("src/bin/output.txt")]
    [InlineData("src/obj/output.txt")]
    [InlineData(".vs/state.txt")]
    [InlineData("TestResults/result.txt")]
    [InlineData("artifacts/build.txt")]
    public async Task Rejects_protected_paths_even_when_permission_would_allow(string path)
    {
        using var workspace = new TemporaryWorkspace();
        var authorizer = new RecordingAuthorizer();
        var tool = new WriteAgentTool(CreateService());
        var json = JsonSerializer.Serialize(new { path, content = "x" });

        var result = await ExecuteAsync(tool, workspace.Root, json, authorizer);

        Assert.False(result.Succeeded);
        Assert.Empty(authorizer.Calls);
        Assert.Contains("protected path", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mutation_tool_default_permission_is_ask()
    {
        var tool = new WriteAgentTool(CreateService());

        Assert.Equal(PermissionDecision.Ask, tool.DefaultPermissionDecision);
    }
}
