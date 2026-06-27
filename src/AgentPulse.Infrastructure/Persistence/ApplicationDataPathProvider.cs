namespace AgentPulse.Infrastructure.Persistence;

public sealed class ApplicationDataPathProvider : IApplicationDataPathProvider
{
    private const string ApplicationDirectoryName = "AgentPulse";
    private const string DataDirectoryName = "data";
    private const string DatabaseFileName = "agentpulse.db";

    private readonly string? _userDataRoot;
    private readonly Func<string> _currentDirectoryProvider;

    public ApplicationDataPathProvider()
        : this(userDataRoot: null, Directory.GetCurrentDirectory)
    {
    }

    public ApplicationDataPathProvider(
        string? userDataRoot,
        Func<string>? currentDirectoryProvider = null)
    {
        _userDataRoot = string.IsNullOrWhiteSpace(userDataRoot)
            ? null
            : Path.GetFullPath(userDataRoot);
        _currentDirectoryProvider = currentDirectoryProvider ?? Directory.GetCurrentDirectory;
    }

    public string ResolveDatabasePath(string? configuredDatabasePath)
    {
        var databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
            ? Path.Combine(
                ResolveUserDataRoot(),
                ApplicationDirectoryName,
                DataDirectoryName,
                DatabaseFileName)
            : ResolveConfiguredPath(configuredDatabasePath);

        var absoluteDatabasePath = Path.GetFullPath(databasePath);
        var directoryPath = Path.GetDirectoryName(absoluteDatabasePath);

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException(
                "The AgentPulse database path must include a valid directory.");
        }

        Directory.CreateDirectory(directoryPath);
        return absoluteDatabasePath;
    }

    private string ResolveConfiguredPath(string configuredDatabasePath)
    {
        var trimmedPath = configuredDatabasePath.Trim();
        return Path.IsPathRooted(trimmedPath)
            ? Path.GetFullPath(trimmedPath)
            : Path.GetFullPath(trimmedPath, _currentDirectoryProvider());
    }

    private string ResolveUserDataRoot()
    {
        if (_userDataRoot is not null)
        {
            return _userDataRoot;
        }

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            return Path.GetFullPath(localApplicationData);
        }

        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException(
                "The current user's application-data directory could not be determined.");
        }

        var fallback = OperatingSystem.IsMacOS()
            ? Path.Combine(userProfile, "Library", "Application Support")
            : Path.Combine(userProfile, ".local", "share");

        return Path.GetFullPath(fallback);
    }
}
