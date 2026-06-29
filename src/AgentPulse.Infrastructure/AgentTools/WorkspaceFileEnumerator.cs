namespace AgentPulse.Infrastructure.AgentTools;

internal static class WorkspaceFileEnumerator
{
    private static readonly HashSet<string> IgnoredDirectories = new(
        [".git", "bin", "obj"],
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static IReadOnlyList<string> EnumerateFiles(
        string basePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(basePath))
        {
            return [basePath];
        }

        if (!Directory.Exists(basePath))
        {
            return [];
        }

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(basePath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(entry))
                {
                    var info = new DirectoryInfo(entry);
                    if (IgnoredDirectories.Contains(info.Name) || !string.IsNullOrEmpty(info.LinkTarget))
                    {
                        continue;
                    }

                    pending.Push(entry);
                }
                else if (File.Exists(entry))
                {
                    var info = new FileInfo(entry);
                    if (string.IsNullOrEmpty(info.LinkTarget))
                    {
                        files.Add(entry);
                    }
                }
            }
        }

        files.Sort(GetPathComparer());
        return files;
    }

    public static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
