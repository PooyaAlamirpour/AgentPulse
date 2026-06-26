using System.Text;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class NamingConventionTests
{
    [Fact]
    public void Repository_contains_no_legacy_product_name_in_paths_or_text()
    {
        var repositoryRoot = FindRepositoryRoot();
        var forbiddenName = string.Concat('m', 'i', 'm', 'o');
        var excludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            "bin",
            "obj",
            "TestResults",
        };

        var entries = EnumerateRepositoryEntries(repositoryRoot, excludedDirectoryNames).ToArray();

        var invalidPaths = entries
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Where(path => path.Contains(forbiddenName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(invalidPaths);

        var invalidFiles = entries
            .Where(File.Exists)
            .Where(path => ContainsText(path, forbiddenName))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(invalidFiles);
    }

    private static IEnumerable<string> EnumerateRepositoryEntries(
        string root,
        IReadOnlySet<string> excludedDirectoryNames)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    if (excludedDirectoryNames.Contains(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    yield return entry;
                    pendingDirectories.Push(entry);
                    continue;
                }

                yield return entry;
            }
        }
    }

    private static bool ContainsText(string path, string forbiddenName)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Contains((byte)0))
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(bytes);
        return text.Contains(forbiddenName, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentPulse.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
