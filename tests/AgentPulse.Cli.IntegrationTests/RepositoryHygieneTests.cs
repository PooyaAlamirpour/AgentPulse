using System.Text.RegularExpressions;

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
        ".trx",
    ];

    [Fact]
    public void Repository_contains_no_database_or_test_result_artifacts()
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
    public void Readme_describes_the_current_tool_calling_surface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var readmePath = Path.Combine(repositoryRoot, "README.md");
        Assert.True(File.Exists(readmePath), "Expected repository file README.md to exist.");

        var readme = File.ReadAllText(readmePath);
        Assert.Contains("## Tool Calling and Agent Loop", readme, StringComparison.Ordinal);
        Assert.Contains("### Read", readme, StringComparison.Ordinal);
        Assert.Contains("### Glob", readme, StringComparison.Ordinal);
        Assert.Contains("### Grep", readme, StringComparison.Ordinal);
        Assert.Contains("MaxToolIterations", readme, StringComparison.Ordinal);
        Assert.Contains("read-only", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## Adding a Tool", readme, StringComparison.Ordinal);
        Assert.Contains("## Build and Test", readme, StringComparison.Ordinal);
        Assert.Contains("## Current Limitations", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_specific_types_do_not_leak_into_domain_or_application()
    {
        var repositoryRoot = FindRepositoryRoot();
        var roots = new[]
        {
            Path.Combine(repositoryRoot, "src", "AgentPulse.Domain"),
            Path.Combine(repositoryRoot, "src", "AgentPulse.Application"),
        };

        var violations = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => File.ReadAllText(path).Contains(
                "OpenAiCompatible",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Local_execution_and_compatibility_documents_are_present()
    {
        var repositoryRoot = FindRepositoryRoot();
        var compatibilityPath = Path.Combine(repositoryRoot, "docs", "cli-compatibility.md");
        var localCliPath = Path.Combine(repositoryRoot, "docs", "local-cli.md");

        Assert.True(File.Exists(compatibilityPath));
        Assert.True(File.Exists(localCliPath));

        var localCli = File.ReadAllText(localCliPath);
        Assert.Contains("dotnet build --no-restore -warnaserror", localCli, StringComparison.Ordinal);
        Assert.Contains("Logging__LogLevel__Default", localCli, StringComparison.Ordinal);
        Assert.Contains("exit code `130`", localCli, StringComparison.Ordinal);
        Assert.Contains("AgentPulse__Tools__MaxToolIterations", localCli, StringComparison.Ordinal);
    }

    [Fact]
    public void Platform_tests_do_not_pass_via_early_return()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoots = new[]
        {
            Path.Combine(repositoryRoot, "tests", "AgentPulse.Cli.IntegrationTests"),
            Path.Combine(repositoryRoot, "tests", "AgentPulse.Cli.TestSupport"),
        };
        var earlyReturnPattern = new Regex(
            @"if\s*\(\s*!?\s*OperatingSystem\.(?:IsWindows|IsLinux|IsMacOS)\(\)\s*\)\s*\{\s*return\s*;",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var violations = testRoots
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Where(path => earlyReturnPattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Windows_interrupt_launcher_uses_a_process_group_without_a_new_console()
    {
        var repositoryRoot = FindRepositoryRoot();
        var supportRoot = Path.Combine(repositoryRoot, "tests", "AgentPulse.Cli.TestSupport");
        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(supportRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.DoesNotContain(
            "CreateNewConsole" + " | " + "CreateNewProcessGroup",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateNewProcessGroup | CreateUnicodeEnvironment",
            source,
            StringComparison.Ordinal);
        Assert.Contains("GenerateConsoleCtrlEvent", source, StringComparison.Ordinal);
        Assert.Contains("SetProcessGroup", source, StringComparison.Ordinal);
    }

    [Fact]
    public void License_and_maintainer_metadata_are_present()
    {
        var repositoryRoot = FindRepositoryRoot();
        var licensePath = Path.Combine(repositoryRoot, "LICENSE");
        var readmePath = Path.Combine(repositoryRoot, "README.md");
        var propsPath = Path.Combine(repositoryRoot, "Directory.Build.props");

        Assert.True(File.Exists(licensePath), "Expected repository file LICENSE to exist.");
        Assert.True(File.Exists(readmePath), "Expected repository file README.md to exist.");
        Assert.True(File.Exists(propsPath), "Expected repository file Directory.Build.props to exist.");

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
    public void Central_build_metadata_identifies_author_and_license()
    {
        var repositoryRoot = FindRepositoryRoot();
        var propsPath = Path.Combine(repositoryRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Expected repository file Directory.Build.props to exist.");
        var props = File.ReadAllText(propsPath);

        Assert.Contains("<Authors>Pooya Alamirpour</Authors>", props, StringComparison.Ordinal);
        Assert.Contains("<PackageLicenseExpression>MIT</PackageLicenseExpression>", props, StringComparison.Ordinal);
        Assert.Contains("<RepositoryType>git</RepositoryType>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void Gitignore_covers_build_database_and_test_artifacts()
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
