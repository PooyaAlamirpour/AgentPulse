namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

public static class OpenAiCompatibleEndpointBuilder
{
    private const int MaximumDecodePasses = 4;

    public static Uri Build(string baseUrl, string chatCompletionsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return Build(new Uri(baseUrl.Trim(), UriKind.Absolute), chatCompletionsPath);
    }

    public static Uri Build(Uri baseUri, string chatCompletionsPath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ValidateRelativePath(chatCompletionsPath);

        var normalizedBase = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        var normalizedPath = NormalizePath(chatCompletionsPath);
        var endpoint = new Uri(new Uri(normalizedBase, UriKind.Absolute), normalizedPath);

        if (!HasSameOrigin(baseUri, endpoint) ||
            !IsWithinBasePath(new Uri(normalizedBase, UriKind.Absolute), endpoint))
        {
            throw new InvalidOperationException(
                "The chat completions path must remain within the configured model endpoint.");
        }

        return endpoint;
    }

    public static void ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The chat completions path must not be empty.");
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith('\\') ||
            Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "The chat completions path must be a relative HTTP path.");
        }

        if (trimmed.Contains('?') || trimmed.Contains('#'))
        {
            throw new InvalidOperationException(
                "The chat completions path must not contain a query string or fragment.");
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("The chat completions path must not be empty.");
        }

        foreach (var segment in segments)
        {
            ValidateSegment(segment);
        }
    }

    private static void ValidateSegment(string segment)
    {
        var current = segment;

        for (var pass = 0; pass < MaximumDecodePasses; pass++)
        {
            EnsureValidPercentEscaping(current);
            RejectUnsafeSegment(current);

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(current);
            }
            catch (UriFormatException exception)
            {
                throw new InvalidOperationException(
                    "The chat completions path contains invalid escaping.",
                    exception);
            }

            RejectUnsafeSegment(decoded);
            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                return;
            }

            current = decoded;
        }

        EnsureValidPercentEscaping(current);
        RejectUnsafeSegment(current);
        if (!string.Equals(Uri.UnescapeDataString(current), current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The chat completions path contains excessive nested escaping.");
        }
    }

    private static void RejectUnsafeSegment(string value)
    {
        if (value is "." or ".." ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Contains('?') ||
            value.Contains('#') ||
            value.Any(static character => character is '\0' or < ' ' or '\u007F') ||
            ContainsEncodedDanger(value))
        {
            throw new InvalidOperationException(
                "The chat completions path contains an unsafe traversal or separator sequence.");
        }
    }

    private static bool ContainsEncodedDanger(string value)
    {
        return value.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("%00", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureValidPercentEscaping(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !IsHex(value[index + 1]) ||
                !IsHex(value[index + 2]))
            {
                throw new InvalidOperationException(
                    "The chat completions path contains invalid escaping.");
            }

            index += 2;
        }
    }

    private static bool IsHex(char value)
    {
        return char.IsAsciiHexDigit(value);
    }

    private static string NormalizePath(string value)
    {
        return string.Join(
            '/',
            value.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool HasSameOrigin(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               left.Port == right.Port;
    }

    private static bool IsWithinBasePath(Uri baseUri, Uri endpoint)
    {
        var basePath = baseUri.AbsolutePath.TrimEnd('/') + "/";
        return endpoint.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal);
    }
}
