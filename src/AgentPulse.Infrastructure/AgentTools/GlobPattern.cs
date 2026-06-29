using System.Text;
using System.Text.RegularExpressions;

namespace AgentPulse.Infrastructure.AgentTools;

internal sealed class GlobPattern
{
    private readonly Regex _regex;

    private GlobPattern(Regex regex)
    {
        _regex = regex;
    }

    public static GlobPattern Create(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (pattern.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Glob pattern contains an invalid null character.", nameof(pattern));
        }

        var normalized = pattern.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Glob pattern cannot be empty.", nameof(pattern));
        }

        if (normalized.Contains('[') || normalized.Contains(']') ||
            normalized.Contains('{') || normalized.Contains('}'))
        {
            throw new ArgumentException(
                "Glob character classes and brace expansion are not supported.",
                nameof(pattern));
        }

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
        try
        {
            return new GlobPattern(new Regex(
                expression.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2)));
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Glob pattern is invalid.", nameof(pattern), exception);
        }
    }

    public bool IsMatch(string relativePath)
    {
        return _regex.IsMatch(relativePath.Replace('\\', '/').TrimStart('/'));
    }
}
