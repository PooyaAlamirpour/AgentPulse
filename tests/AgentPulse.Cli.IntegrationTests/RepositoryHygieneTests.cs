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

    [Theory]
    [InlineData("ModelProviders/Xiaomi/XiaomiChatRequestMapper.cs")]
    [InlineData("ModelProviders/Xiaomi/XiaomiChatRequestDto.cs")]
    [InlineData("ModelProviders/Xiaomi/TimedReadStream.cs")]
    [InlineData("ModelProviders/OpenAiCompatible/TimedReadStream.cs")]
    [InlineData("ModelProviders/OpenAiCompatible/OpenAiCompatibleCredentialValidator.cs")]
    public void Deprecated_xiaomi_transport_artifacts_are_absent(string relativeArtifactPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var pathSegments = relativeArtifactPath.Split('/');
        var artifactPath = Path.Combine(
            [repositoryRoot, "src", "AgentPulse.Infrastructure", .. pathSegments]);

        Assert.False(
            File.Exists(artifactPath),
            $"Deprecated infrastructure artifact {Path.GetFileName(artifactPath)} must not exist.");
    }

    [Fact]
    public void Active_documentation_marks_phase_eight_complete_with_final_vertical_flow()
    {
        var repositoryRoot = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var map = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "node-to-dotnet-map.md"));

        Assert.Contains("| 8 | ✅ |", readme, StringComparison.Ordinal);
        Assert.Contains("Final Vertical Prompt Flow and Session Reliability", readme, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatibleChatModelClient", readme, StringComparison.Ordinal);
        Assert.Contains("--dir", map, StringComparison.Ordinal);
        Assert.Contains("--model", map, StringComparison.Ordinal);
        Assert.Contains("--session", map, StringComparison.Ordinal);
        Assert.Contains("stdin", map, StringComparison.Ordinal);
        Assert.DoesNotContain("FakeChatModelClient", map, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider واقعی فقط در فاز ۷", map, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_documentation_records_phase_eight_cli_continuation_as_complete()
    {
        var repositoryRoot = FindRepositoryRoot();
        var map = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "node-to-dotnet-map.md"));
        var phaseFour = ExtractSection(map, "### فاز ۴", "### فاز ۵");
        var phaseEight = ExtractSection(map, "### فاز ۸", "### فاز ۹");

        Assert.Contains("در سطح Application", phaseFour, StringComparison.Ordinal);
        Assert.DoesNotContain("--session", phaseFour, StringComparison.Ordinal);
        Assert.Contains("--session", phaseEight, StringComparison.Ordinal);
        Assert.DoesNotContain("آینده", phaseEight, StringComparison.Ordinal);
        Assert.Contains("Release اتمیک مالک‌محور", phaseEight, StringComparison.Ordinal);
    }

    [Fact]
    public void License_and_maintainer_metadata_are_present()
    {
        var repositoryRoot = FindRepositoryRoot();
        var licensePath = Path.Combine(repositoryRoot, "LICENSE");
        var readmePath = Path.Combine(repositoryRoot, "README.md");
        var propsPath = Path.Combine(repositoryRoot, "Directory.Build.props");

        Assert.True(
            File.Exists(licensePath),
            "Expected repository file LICENSE to exist.");
        Assert.True(
            File.Exists(readmePath),
            "Expected repository file README.md to exist.");
        Assert.True(
            File.Exists(propsPath),
            "Expected repository file Directory.Build.props to exist.");

        var license = File.ReadAllText(licensePath);
        var readme = File.ReadAllText(readmePath);
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
        var propsPath = Path.Combine(repositoryRoot, "Directory.Build.props");
        Assert.True(
            File.Exists(propsPath),
            "Expected repository file Directory.Build.props to exist.");
        var props = File.ReadAllText(propsPath);

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
