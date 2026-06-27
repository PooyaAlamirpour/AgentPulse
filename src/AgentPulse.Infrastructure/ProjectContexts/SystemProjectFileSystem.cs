using System.Security;
using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Infrastructure.ProjectContexts;

public sealed class SystemProjectFileSystem : IProjectFileSystem
{
    public string GetCurrentDirectory()
    {
        try
        {
            return NormalizeAbsolutePath(Environment.CurrentDirectory);
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            throw InvalidPath("The current directory is invalid.", exception);
        }
        catch (Exception exception) when (IsAccessException(exception))
        {
            throw AccessDenied("The current directory cannot be accessed.", exception);
        }
    }

    public string NormalizePath(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        try
        {
            var absolutePath = Path.IsPathFullyQualified(path)
                ? path
                : Path.Combine(baseDirectory, path);

            return NormalizeAbsolutePath(absolutePath);
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            throw InvalidPath($"The path '{path}' is invalid.", exception);
        }
        catch (Exception exception) when (IsAccessException(exception))
        {
            throw AccessDenied($"The path '{path}' cannot be accessed.", exception);
        }
    }

    public ProjectPathEntryKind GetEntryKind(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? ProjectPathEntryKind.Directory
                : ProjectPathEntryKind.File;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ProjectPathEntryKind.Missing;
        }
        catch (Exception exception) when (IsInvalidPathException(exception))
        {
            throw InvalidPath($"The path '{path}' is invalid.", exception);
        }
        catch (Exception exception) when (IsAccessException(exception))
        {
            throw AccessDenied($"The path '{path}' cannot be accessed.", exception);
        }
    }

    public string CanonicalizePath(string normalizedAbsolutePath, ProjectPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAbsolutePath);

        var canonical = normalizedAbsolutePath;

        if (platform == ProjectPlatform.Windows)
        {
            canonical = canonical.Replace('\\', '/');
            canonical = canonical.ToUpperInvariant();

            if (canonical.Length > 3 || !IsWindowsDriveRoot(canonical))
            {
                canonical = canonical.TrimEnd('/');
            }
        }
        else
        {
            canonical = canonical.TrimEnd(Path.DirectorySeparatorChar);
        }

        if (canonical.Length == 0)
        {
            canonical = platform == ProjectPlatform.Windows ? "/" : Path.DirectorySeparatorChar.ToString();
        }

        return canonical;
    }

    public bool IsPathWithin(string path, string candidateParent, ProjectPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateParent);

        var canonicalPath = CanonicalizePath(path, platform);
        var canonicalParent = CanonicalizePath(candidateParent, platform);
        var comparison = platform == ProjectPlatform.Windows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(canonicalPath, canonicalParent, comparison))
        {
            return true;
        }

        var separator = platform == ProjectPlatform.Windows ? '/' : Path.DirectorySeparatorChar;
        var parentPrefix = canonicalParent.EndsWith(separator)
            ? canonicalParent
            : string.Concat(canonicalParent, separator);

        return canonicalPath.StartsWith(parentPrefix, comparison);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);

        if (root is not null && string.Equals(fullPath, root, PathComparison))
        {
            return fullPath;
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsWindowsDriveRoot(string path)
    {
        return path.Length == 3 &&
            char.IsAsciiLetter(path[0]) &&
            path[1] == ':' &&
            path[2] == '/';
    }

    private static bool IsInvalidPathException(Exception exception)
    {
        return exception is ArgumentException or NotSupportedException or PathTooLongException;
    }

    private static bool IsAccessException(Exception exception)
    {
        return exception is UnauthorizedAccessException or SecurityException or IOException;
    }

    private static ProjectContextException InvalidPath(string message, Exception exception)
    {
        return new ProjectContextException(ProjectContextErrorCode.InvalidPath, message, exception);
    }

    private static ProjectContextException AccessDenied(string message, Exception exception)
    {
        return new ProjectContextException(ProjectContextErrorCode.PathAccessDenied, message, exception);
    }
}
