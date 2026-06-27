namespace AgentPulse.Infrastructure.Credentials;

public sealed class ProviderCredentialStoreOptions
{
    public const string SectionName = "AgentPulse:Security";

    public ProviderCredentialStoreOptions(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public static string GetDefaultRootPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidOperationException(
                    "The current user's local application data directory could not be resolved.");
            }

            localApplicationData = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(localApplicationData, "AgentPulse", "security");
    }
}
