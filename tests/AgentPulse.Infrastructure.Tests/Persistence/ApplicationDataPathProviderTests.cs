using AgentPulse.Infrastructure.Persistence;

namespace AgentPulse.Infrastructure.Tests.Persistence;

public sealed class ApplicationDataPathProviderTests
{
    [Fact]
    public void Default_path_is_absolute_user_scoped_stable_and_creates_directory()
    {
        using var root = new TemporaryDirectory("AgentPulse Path Tests");
        var providerFromFirstWorkingDirectory = new ApplicationDataPathProvider(
            root.Path,
            () => Path.Combine(root.Path, "working-one"));
        var providerFromSecondWorkingDirectory = new ApplicationDataPathProvider(
            root.Path,
            () => Path.Combine(root.Path, "working-two"));

        var firstPath = providerFromFirstWorkingDirectory.ResolveDatabasePath(null);
        var secondPath = providerFromSecondWorkingDirectory.ResolveDatabasePath(null);

        var expectedPath = Path.Combine(
            root.Path,
            "AgentPulse",
            "data",
            "agentpulse.db");
        Assert.Equal(Path.GetFullPath(expectedPath), firstPath);
        Assert.Equal(firstPath, secondPath);
        Assert.True(Path.IsPathFullyQualified(firstPath));
        Assert.True(Directory.Exists(Path.GetDirectoryName(firstPath)));
        Assert.False(firstPath.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Absolute_override_with_spaces_is_preserved_and_directory_is_created()
    {
        using var root = new TemporaryDirectory("AgentPulse Absolute Override");
        var configuredPath = Path.Combine(root.Path, "database folder", "custom database.db");
        var provider = new ApplicationDataPathProvider(root.Path);

        var result = provider.ResolveDatabasePath(configuredPath);

        Assert.Equal(Path.GetFullPath(configuredPath), result);
        Assert.True(Directory.Exists(Path.GetDirectoryName(result)));
    }

    [Fact]
    public void Relative_override_is_normalized_against_current_directory_provider()
    {
        using var root = new TemporaryDirectory("AgentPulse Relative Override");
        var workingDirectory = Path.Combine(root.Path, "working directory");
        Directory.CreateDirectory(workingDirectory);
        var provider = new ApplicationDataPathProvider(root.Path, () => workingDirectory);

        var result = provider.ResolveDatabasePath(Path.Combine("state", "agentpulse.db"));

        Assert.Equal(
            Path.Combine(workingDirectory, "state", "agentpulse.db"),
            result);
        Assert.True(Directory.Exists(Path.Combine(workingDirectory, "state")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
