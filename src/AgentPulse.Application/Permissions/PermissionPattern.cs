using System.Text;
using System.Text.RegularExpressions;

namespace AgentPulse.Application.Permissions;

internal static class PermissionPattern
{
    private static readonly char[] UnsupportedWildcardCharacters = ['?', '[', ']', '{', '}'];

    public static string NormalizeSelector(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "*")
        {
            return normalized;
        }

        if (normalized.Any(static character =>
                !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "Permission tool and operation selectors must be an exact name or '*'.",
                parameterName);
        }

        return normalized;
    }

    public static string NormalizeTargetPattern(string value, string parameterName)
    {
        var normalized = NormalizeTargetCore(value, parameterName);
        if (normalized.IndexOfAny(UnsupportedWildcardCharacters) >= 0 ||
            normalized.Contains("***", StringComparison.Ordinal) ||
            normalized.Split('/').Any(static segment => segment.Contains("**", StringComparison.Ordinal) && segment != "**"))
        {
            throw new ArgumentException(
                "Permission target patterns support only '*' and complete '**' path segments.",
                parameterName);
        }

        return normalized == "**" ? "*" : normalized;
    }

    public static string NormalizeRequestTarget(string value, string parameterName)
    {
        return NormalizeTargetCore(value, parameterName);
    }

    public static bool IsMatch(string pattern, string target)
    {
        if (pattern is "*" or "**")
        {
            return true;
        }

        var candidate = !pattern.Contains('/', StringComparison.Ordinal) && ContainsWildcard(pattern)
            ? GetFileName(target)
            : target;
        var expression = BuildExpression(pattern);
        var options = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows())
        {
            options |= RegexOptions.IgnoreCase;
        }

        return Regex.IsMatch(candidate, expression, options, TimeSpan.FromSeconds(1));
    }

    public static bool EqualsTarget(string left, string right)
    {
        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static bool ContainsWildcard(string value) => value.Contains('*', StringComparison.Ordinal);

    private static string BuildExpression(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] != '*')
            {
                builder.Append(Regex.Escape(pattern[index].ToString()));
                continue;
            }

            var isDouble = index + 1 < pattern.Length && pattern[index + 1] == '*';
            if (!isDouble)
            {
                builder.Append("[^/]*");
                continue;
            }

            index++;
            if (index + 1 < pattern.Length && pattern[index + 1] == '/')
            {
                index++;
                builder.Append("(?:.*/)?");
            }
            else
            {
                builder.Append(".*");
            }
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static string NormalizeTargetCore(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || HasWindowsDrivePrefix(normalized))
        {
            throw new ArgumentException("Permission targets must be workspace-relative.", parameterName);
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Permission targets must not contain control characters.", parameterName);
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment == ".."))
        {
            throw new ArgumentException("Permission targets must not contain parent traversal.", parameterName);
        }

        normalized = string.Join('/', segments.Where(static segment => segment != "."));
        return string.IsNullOrEmpty(normalized) ? "." : normalized;
    }

    private static bool HasWindowsDrivePrefix(string value)
    {
        return value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
    }

    private static string GetFileName(string target)
    {
        var separator = target.LastIndexOf('/');
        return separator < 0 ? target : target[(separator + 1)..];
    }
}
