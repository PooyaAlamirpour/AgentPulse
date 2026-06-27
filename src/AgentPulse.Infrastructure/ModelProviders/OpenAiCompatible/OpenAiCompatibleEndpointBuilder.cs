namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

public static class OpenAiCompatibleEndpointBuilder
{
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

        if (!HasSameOrigin(baseUri, endpoint))
        {
            throw new InvalidOperationException(
                "The chat completions path must remain on the configured model endpoint origin.");
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
        var relativeCandidate = trimmed.TrimStart('/');
        if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.StartsWith('\\') ||
            string.IsNullOrWhiteSpace(relativeCandidate) ||
            Uri.TryCreate(relativeCandidate, UriKind.Absolute, out _) ||
            trimmed.Contains('\\'))
        {
            throw new InvalidOperationException(
                "The chat completions path must be a relative HTTP path.");
        }

        if (trimmed.Contains('?') ||
            trimmed.Contains('#'))
        {
            throw new InvalidOperationException(
                "The chat completions path must not contain a query string or fragment.");
        }

        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException exception)
            {
                throw new InvalidOperationException(
                    "The chat completions path contains invalid escaping.",
                    exception);
            }

            if (decoded is "." or "..")
            {
                throw new InvalidOperationException(
                    "The chat completions path must not contain traversal segments.");
            }
        }
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
}
