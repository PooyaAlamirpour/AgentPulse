using System.Security.Cryptography;
using System.Text;
using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.Credentials;

public sealed record ProviderCredentialScope
{
    private ProviderCredentialScope(
        string scheme,
        string host,
        int port,
        OpenAiCompatibleAuthenticationMode authenticationMode,
        string? apiKeyHeaderName)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        AuthenticationMode = authenticationMode;
        ApiKeyHeaderName = apiKeyHeaderName;
        CanonicalValue = authenticationMode == OpenAiCompatibleAuthenticationMode.ApiKeyHeader
            ? $"{scheme}://{host}:{port}|{authenticationMode}|{apiKeyHeaderName}"
            : $"{scheme}://{host}:{port}|{authenticationMode}";
        FileId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalValue)))
            .ToLowerInvariant();
    }

    public string Scheme { get; }

    public string Host { get; }

    public int Port { get; }

    public OpenAiCompatibleAuthenticationMode AuthenticationMode { get; }

    public string? ApiKeyHeaderName { get; }

    public string CanonicalValue { get; }

    public string FileId { get; }

    public bool IsDefaultEndpoint =>
        string.Equals(Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.Equals(Host, "api.openai.com", StringComparison.Ordinal) &&
        Port == 443 &&
        AuthenticationMode == OpenAiCompatibleAuthenticationMode.Bearer;

    public static ProviderCredentialScope Default { get; } = Create(
        new Uri(OpenAiCompatibleModelOptions.DefaultBaseUrl, UriKind.Absolute),
        OpenAiCompatibleAuthenticationMode.Bearer,
        apiKeyHeaderName: null);

    public static ProviderCredentialScope Create(
        Uri baseUri,
        OpenAiCompatibleAuthenticationMode authenticationMode,
        string? apiKeyHeaderName)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The credential scope URI must be absolute.", nameof(baseUri));
        }

        if (!Enum.IsDefined(authenticationMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(authenticationMode),
                authenticationMode,
                "Unknown authentication mode.");
        }

        var normalizedHeader = authenticationMode ==
            OpenAiCompatibleAuthenticationMode.ApiKeyHeader
                ? NormalizeRequired(apiKeyHeaderName, nameof(apiKeyHeaderName)).ToLowerInvariant()
                : null;
        var normalizedScheme = baseUri.Scheme.ToLowerInvariant();
        var normalizedHost = baseUri.IdnHost.ToLowerInvariant();
        var effectivePort = baseUri.IsDefaultPort
            ? normalizedScheme == Uri.UriSchemeHttps ? 443 : 80
            : baseUri.Port;

        return new ProviderCredentialScope(
            normalizedScheme,
            normalizedHost,
            effectivePort,
            authenticationMode,
            normalizedHeader);
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }
}
