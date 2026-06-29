using AgentPulse.Application.Workspaces;

namespace AgentPulse.Infrastructure.Workspaces;

public sealed class WorkspacePathResolver : IWorkspacePathResolver
{
    public string Resolve(string workspaceRoot, string? requestedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        var candidate = string.IsNullOrWhiteSpace(requestedPath)
            ? root
            : Path.GetFullPath(requestedPath, root);

        if (!IsInside(root, candidate))
        {
            throw new UnauthorizedAccessException(
                "The requested path is outside the active workspace.");
        }

        EnsureExistingLinksRemainInside(root, candidate);
        return candidate;
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, comparison);
    }

    private static void EnsureExistingLinksRemainInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info is null || string.IsNullOrEmpty(info.LinkTarget))
            {
                continue;
            }

            FileSystemInfo? resolved;
            try
            {
                resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException exception)
            {
                throw new UnauthorizedAccessException(
                    "The requested path contains an unresolved symbolic link or junction.",
                    exception);
            }

            if (resolved is null || !IsInside(root, Path.GetFullPath(resolved.FullName)))
            {
                throw new UnauthorizedAccessException(
                    "The requested path resolves outside the active workspace.");
            }
        }
    }
}
