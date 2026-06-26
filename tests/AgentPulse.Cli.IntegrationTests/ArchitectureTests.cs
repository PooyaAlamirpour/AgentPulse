using System.Xml.Linq;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Project_references_follow_clean_architecture_direction()
    {
        var root = FindRepositoryRoot();

        AssertReferences(root, "src/AgentPulse.Domain/AgentPulse.Domain.csproj", []);
        AssertReferences(root, "src/AgentPulse.Application/AgentPulse.Application.csproj", ["AgentPulse.Domain"]);
        AssertReferences(root, "src/AgentPulse.Infrastructure/AgentPulse.Infrastructure.csproj", ["AgentPulse.Application"]);
        AssertReferences(root, "src/AgentPulse.Cli/AgentPulse.Cli.csproj", ["AgentPulse.Application", "AgentPulse.Infrastructure"]);
    }

    [Fact]
    public void Shared_build_settings_target_dotnet_8_and_treat_warnings_as_errors()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "Directory.Build.props"));

        Assert.Equal("net8.0", FindProperty(document, "TargetFramework"));
        Assert.Equal("enable", FindProperty(document, "Nullable"));
        Assert.Equal("true", FindProperty(document, "TreatWarningsAsErrors"));
    }

    private static void AssertReferences(
        string root,
        string relativeProjectPath,
        IReadOnlyCollection<string> expectedReferences)
    {
        var document = XDocument.Load(Path.Combine(root, relativeProjectPath));
        var actualReferences = document
            .Descendants("ProjectReference")
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static value => value is not null)
            .Select(static value => Path.GetFileNameWithoutExtension(value!.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedReferences.Order(StringComparer.Ordinal),
            actualReferences);
    }

    private static string FindProperty(XDocument document, string propertyName)
    {
        return document.Descendants(propertyName).Single().Value;
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
