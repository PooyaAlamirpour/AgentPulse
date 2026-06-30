using System.Text;
using System.Text.RegularExpressions;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Workspaces;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Infrastructure.Mutations;

internal sealed class ProtectedPathPolicy : IProtectedPathPolicy
{
    private static readonly string[] RequiredPatterns =
    [
        ".git",
        ".git/**",
        "**/bin",
        "**/bin/**",
        "**/obj",
        "**/obj/**",
        ".vs",
        ".vs/**",
        "**/TestResults",
        "**/TestResults/**",
        "**/artifacts",
        "**/artifacts/**",
    ];

    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IReadOnlyList<Regex> _patterns;
    private readonly ILogger<ProtectedPathPolicy> _logger;

    public ProtectedPathPolicy(
        IWorkspacePathResolver pathResolver,
        MutationToolOptions options,
        ILogger<ProtectedPathPolicy> logger)
    {
        options.Validate();
        _pathResolver = pathResolver;
        _logger = logger;
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _patterns = RequiredPatterns
            .Concat(options.ProtectedPatterns)
            .Distinct(comparer)
            .Select(CreatePattern)
            .ToArray();
    }

    public ResolvedMutationPath ResolveAndValidate(string workspaceRoot, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new MutationValidationException("Mutation paths must not be empty.");
        }

        var normalizedRequest = requestedPath.Replace('\\', '/');
        if (IsPortableRooted(normalizedRequest))
        {
            throw new MutationValidationException("Mutation paths must be workspace-relative.");
        }

        if (normalizedRequest.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == ".."))
        {
            throw new MutationValidationException("Mutation paths must not contain traversal.");
        }

        string fullPath;
        try
        {
            fullPath = _pathResolver.Resolve(workspaceRoot, normalizedRequest);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            throw new MutationValidationException(exception.Message);
        }

        var root = Path.GetFullPath(workspaceRoot);
        fullPath = CanonicalizeExistingLinks(root, fullPath);
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative is "." or "")
        {
            throw new MutationValidationException("The workspace root cannot be mutated as a file.");
        }

        if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == ".."))
        {
            throw new MutationValidationException("Mutation paths must not contain traversal.");
        }

        if (_patterns.Any(pattern => pattern.IsMatch(relative)))
        {
            _logger.LogWarning("Protected path rejected for mutation target {Target}.", relative);
            throw new MutationValidationException(
                $"Mutation of protected path '{relative}' is not allowed.");
        }

        return new ResolvedMutationPath(fullPath, relative);
    }

    private static string CanonicalizeExistingLinks(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info is not null && !string.IsNullOrEmpty(info.LinkTarget))
            {
                FileSystemInfo? resolved;
                try
                {
                    resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                }
                catch (IOException exception)
                {
                    throw new MutationValidationException(
                        $"The mutation path contains an unresolved symbolic link or junction: {exception.GetType().Name}.");
                }

                if (resolved is null)
                {
                    throw new MutationValidationException(
                        "The mutation path contains an unresolved symbolic link or junction.");
                }

                current = Path.GetFullPath(resolved.FullName);
                if (!IsInside(root, current))
                {
                    throw new MutationValidationException(
                        "The mutation path resolves outside the active workspace.");
                }
            }

            if (index < segments.Length - 1 && File.Exists(current) && !Directory.Exists(current))
            {
                throw new MutationValidationException(
                    "The mutation path contains a file where a directory is required.");
            }
        }

        return Path.GetFullPath(current);
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

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static bool IsPortableRooted(string path) =>
        Path.IsPathRooted(path) ||
        path.StartsWith('/') ||
        path.StartsWith("//", StringComparison.Ordinal) ||
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static Regex CreatePattern(string pattern)
    {
        var normalized = pattern.Trim().Replace('\\', '/').TrimStart('/');
        var expression = new StringBuilder("^");
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (character == '*')
            {
                var isDouble = index + 1 < normalized.Length && normalized[index + 1] == '*';
                if (isDouble)
                {
                    index++;
                    var followedBySlash = index + 1 < normalized.Length && normalized[index + 1] == '/';
                    if (followedBySlash)
                    {
                        index++;
                        expression.Append("(?:.*/)?");
                    }
                    else
                    {
                        expression.Append(".*");
                    }
                }
                else
                {
                    expression.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                expression.Append("[^/]");
            }
            else
            {
                expression.Append(Regex.Escape(character.ToString()));
            }
        }

        expression.Append('$');
        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (OperatingSystem.IsWindows())
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(expression.ToString(), options, TimeSpan.FromSeconds(2));
    }
}
