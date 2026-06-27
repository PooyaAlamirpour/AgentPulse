using AgentPulse.Application.ProjectContexts;
using AgentPulse.Infrastructure.ProjectContexts;

namespace AgentPulse.Infrastructure.Tests.ProjectContexts;

public sealed class SystemProjectFileSystemTests
{
    [Fact]
    public void Relative_path_is_resolved_normalized_and_made_absolute()
    {
        var fileSystem = new SystemProjectFileSystem();
        var root = CreateTemporaryDirectory();
        var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;

        try
        {
            var actual = fileSystem.NormalizePath(
                Path.Combine("child", ".", "nested", ".."),
                root);

            Assert.Equal(Path.TrimEndingDirectorySeparator(child), actual);
            Assert.True(Path.IsPathFullyQualified(actual));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Entry_kind_distinguishes_directory_file_and_missing_path()
    {
        var fileSystem = new SystemProjectFileSystem();
        var root = CreateTemporaryDirectory();
        var file = Path.Combine(root, "file.txt");
        File.WriteAllText(file, "content");

        try
        {
            Assert.Equal(ProjectPathEntryKind.Directory, fileSystem.GetEntryKind(root));
            Assert.Equal(ProjectPathEntryKind.File, fileSystem.GetEntryKind(file));
            Assert.Equal(
                ProjectPathEntryKind.Missing,
                fileSystem.GetEntryKind(Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Canonicalization_and_containment_follow_requested_platform_rules()
    {
        var fileSystem = new SystemProjectFileSystem();

        Assert.Equal(
            "C:/WORK/PROJECT",
            fileSystem.CanonicalizePath("C:\\Work\\Project\\", ProjectPlatform.Windows));
        Assert.True(fileSystem.IsPathWithin(
            "C:\\Work\\Project\\src",
            "c:\\work\\project",
            ProjectPlatform.Windows));
        Assert.False(fileSystem.IsPathWithin(
            "/work/Project/src",
            "/work/project",
            ProjectPlatform.Linux));
        Assert.False(fileSystem.IsPathWithin(
            "/work/project-other",
            "/work/project",
            ProjectPlatform.Linux));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AgentPulse.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
