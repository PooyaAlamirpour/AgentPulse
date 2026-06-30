using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.Mutations;
using AgentPulse.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class ProtectedPathPolicyTests
{
    [Theory]
    [InlineData(".git/config")]
    [InlineData("nested/bin/app.dll")]
    [InlineData("nested/obj/cache.bin")]
    [InlineData(".vs/state.json")]
    [InlineData("nested/TestResults/result.xml")]
    [InlineData("nested/artifacts/output.txt")]
    [InlineData("nested\\bin\\output.txt")]
    public void Required_protected_paths_are_rejected(string path)
    {
        using var workspace = new TemporaryWorkspace();
        var policy = CreatePolicy();

        var exception = Assert.Throws<MutationValidationException>(() =>
            policy.ResolveAndValidate(workspace.Root, path));

        Assert.Contains("protected path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("objectives/readme.txt")]
    [InlineData("binary/readme.txt")]
    [InlineData("artifact/readme.txt")]
    [InlineData("src/TestResult/readme.txt")]
    [InlineData("src\\safe\\readme.txt")]
    public void Similarly_named_safe_paths_are_allowed(string path)
    {
        using var workspace = new TemporaryWorkspace();
        var policy = CreatePolicy();

        var result = policy.ResolveAndValidate(workspace.Root, path);

        Assert.Equal(path.Replace('\\', '/'), result.RelativePath);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    public void Traversal_and_portable_absolute_paths_are_rejected(string path)
    {
        using var workspace = new TemporaryWorkspace();
        var policy = CreatePolicy();

        Assert.Throws<MutationValidationException>(() =>
            policy.ResolveAndValidate(workspace.Root, path));
    }

    [Fact]
    public void Mandatory_patterns_cannot_be_disabled_by_empty_configuration()
    {
        using var workspace = new TemporaryWorkspace();
        var options = CreateOptions();
        options.ProtectedPatterns = [];
        var policy = CreatePolicy(options);

        Assert.Throws<MutationValidationException>(() =>
            policy.ResolveAndValidate(workspace.Root, ".git/config"));
    }

    [Fact]
    public void Symlink_or_junction_escape_is_rejected_when_supported()
    {
        using var workspace = new TemporaryWorkspace();
        var outside = Path.Combine(Path.GetTempPath(), $"agentpulse-outside-{Guid.NewGuid():N}");
        var link = workspace.PathOf("link");
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var policy = CreatePolicy();

            Assert.Throws<MutationValidationException>(() =>
                policy.ResolveAndValidate(workspace.Root, "link/file.txt"));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Invalid_custom_patterns_are_rejected()
    {
        var options = CreateOptions();
        options.ProtectedPatterns = ["../outside/**"];

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("traversal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_file_in_parent_position_is_rejected()
    {
        using var workspace = new TemporaryWorkspace();
        File.WriteAllText(workspace.PathOf("parent"), "file");
        var policy = CreatePolicy();

        var exception = Assert.Throws<MutationValidationException>(() =>
            policy.ResolveAndValidate(workspace.Root, "parent/child.txt"));

        Assert.Contains("directory is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inside_workspace_symlink_is_canonicalized_when_supported()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(workspace.PathOf("actual"));
        var link = workspace.PathOf("alias");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, workspace.PathOf("actual"));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var result = CreatePolicy().ResolveAndValidate(workspace.Root, "alias/file.txt");

            Assert.Equal("actual/file.txt", result.RelativePath);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    private static ProtectedPathPolicy CreatePolicy(MutationToolOptions? options = null) => new(
        new WorkspacePathResolver(),
        options ?? CreateOptions(),
        NullLogger<ProtectedPathPolicy>.Instance);
}
