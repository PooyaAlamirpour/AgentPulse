namespace AgentPulse.Cli.IntegrationTests;

public sealed class RepositoryHygieneTests
{
    private static readonly string[] DatabaseExtensions =
    [
        ".db",
        ".db-shm",
        ".db-wal",
        ".sqlite",
        ".sqlite3",
    ];

    [Fact]
    public void Repository_contains_no_sqlite_database_artifacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var artifacts = Directory
            .EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories)
            .Where(path => DatabaseExtensions.Any(extension =>
                path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(artifacts);
    }

    [Fact]
    public void Active_documentation_marks_phase_seven_complete_with_single_generic_runtime_client()
    {
        var repositoryRoot = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var map = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "node-to-dotnet-map.md"));

        Assert.Contains("| 7 | ✅ |", readme, StringComparison.Ordinal);
        Assert.Contains("OpenAI-Compatible Provider Generalization and Hardening", readme, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatibleChatModelClient", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("FakeChatModelClient", map, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider واقعی فقط در فاز ۷", map, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_documentation_keeps_cli_session_option_in_phase_eight_only()
    {
        var repositoryRoot = FindRepositoryRoot();
        var map = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "node-to-dotnet-map.md"));
        var phaseFour = ExtractSection(map, "### فاز ۴", "### فاز ۵");
        var phaseEight = ExtractSection(map, "### فاز ۸", "### فاز ۹");

        Assert.Contains("در سطح Application", phaseFour, StringComparison.Ordinal);
        Assert.DoesNotContain("--session", phaseFour, StringComparison.Ordinal);
        Assert.Contains("--session", phaseEight, StringComparison.Ordinal);
        Assert.Contains("آینده", phaseEight, StringComparison.Ordinal);
    }

    [Fact]
    public void License_and_maintainer_metadata_are_present()
    {
        var repositoryRoot = FindRepositoryRoot();
        var licensePath = Path.Combine(repositoryRoot, "LICENSE");
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var license = File.ReadAllText(licensePath);

        Assert.True(File.Exists(licensePath));
        Assert.Contains("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Copyright (c) 2026 Pooya Alamirpour", license, StringComparison.Ordinal);
        Assert.Contains("## Maintainer", readme, StringComparison.Ordinal);
        Assert.Contains("Pooya Alamirpour", readme, StringComparison.Ordinal);
        Assert.Contains("@Alamirpour", readme, StringComparison.Ordinal);
        Assert.Contains("https://t.me/Alamirpour", readme, StringComparison.Ordinal);
        Assert.Contains("[MIT License](LICENSE)", readme, StringComparison.Ordinal);

        var contributingIndex = readme.IndexOf("## Contributing", StringComparison.Ordinal);
        var maintainerIndex = readme.IndexOf("## Maintainer", StringComparison.Ordinal);
        var licenseIndex = readme.IndexOf("## License", StringComparison.Ordinal);

        Assert.True(contributingIndex >= 0);
        Assert.True(maintainerIndex > contributingIndex);
        Assert.True(licenseIndex > maintainerIndex);
    }

    [Fact]
    public void Central_build_metadata_identifies_author_and_mit_license()
    {
        var repositoryRoot = FindRepositoryRoot();
        var props = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Build.props"));

        Assert.Contains("<Authors>Pooya Alamirpour</Authors>", props, StringComparison.Ordinal);
        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", props, StringComparison.Ordinal);
        Assert.Contains("<RepositoryType>git</RepositoryType>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void Gitignore_covers_sqlite_and_test_artifacts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var gitignore = File.ReadAllLines(Path.Combine(repositoryRoot, ".gitignore"));

        Assert.Contains("**/*.db", gitignore);
        Assert.Contains("**/*.db-shm", gitignore);
        Assert.Contains("**/*.db-wal", gitignore);
        Assert.Contains("**/*.sqlite", gitignore);
        Assert.Contains("**/*.sqlite3", gitignore);
        Assert.Contains("**/bin/", gitignore);
        Assert.Contains("**/obj/", gitignore);
        Assert.Contains("TestResults/", gitignore);
        Assert.Contains("*.trx", gitignore);
    }

    private static string ExtractSection(string document, string startHeading, string nextHeading)
    {
        var start = document.IndexOf(startHeading, StringComparison.Ordinal);
        var end = document.IndexOf(nextHeading, start + startHeading.Length, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Could not find section '{startHeading}'.");
        Assert.True(end > start, $"Could not find section boundary '{nextHeading}'.");

        return document[start..end];
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
