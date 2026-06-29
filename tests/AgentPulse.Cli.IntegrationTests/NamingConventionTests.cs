using System.Text.RegularExpressions;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class NamingConventionTests
{
    [Fact]
    public void Source_namespaces_use_the_agentpulse_root()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var namespacePattern = new Regex(
            @"^\s*namespace\s+([^;\s]+)\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        var invalid = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Match = namespacePattern.Match(File.ReadAllText(path)),
            })
            .Where(value => value.Match.Success)
            .Where(value => !value.Match.Groups[1].Value.StartsWith(
                "AgentPulse",
                StringComparison.Ordinal))
            .Select(value => Path.GetRelativePath(repositoryRoot, value.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(invalid);
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
