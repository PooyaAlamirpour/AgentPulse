using System.Net;
using AgentPulse.Infrastructure.Credentials;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

public sealed class OpenAiCompatibleModelOptions
{
    private static readonly HashSet<string> ForbiddenApiKeyHeaders = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
        "Proxy-Connection",
        "Upgrade",
        "Trailer",
        "TE",
    };

    public const string SectionName = "AgentPulse:Model";
    public const string XiaomiDefaultBaseUrl = "https://api.xiaomimimo.com/v1";
    public const string XiaomiDefaultModel = "mimo-v2.5-pro";
    public const string XiaomiDefaultApiKeyEnvironmentVariable = "MIMO_API_KEY";

    public string BaseUrl { get; set; } = XiaomiDefaultBaseUrl;

    public string ChatCompletionsPath { get; set; } = "chat/completions";

    public string Model { get; set; } = XiaomiDefaultModel;

    public OpenAiCompatibleAuthenticationMode AuthenticationMode { get; set; } =
        OpenAiCompatibleAuthenticationMode.ApiKeyHeader;

    public string ApiKeyHeaderName { get; set; } = "api-key";

    public string ApiKeyEnvironmentVariable { get; set; } =
        XiaomiDefaultApiKeyEnvironmentVariable;

    public int MaxCompletionTokens { get; set; } = 4096;

    public string ThinkingMode { get; set; } = "disabled";

    public bool IncludeThinkingConfiguration { get; set; } = true;

    public TimeSpan FirstByteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        var baseUri = ValidateBaseUrl(BaseUrl);
        OpenAiCompatibleEndpointBuilder.ValidateRelativePath(ChatCompletionsPath);

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("The OpenAI-compatible model name must not be empty.");
        }

        if (!Enum.IsDefined(AuthenticationMode))
        {
            throw new InvalidOperationException("The OpenAI-compatible authentication mode is not supported.");
        }

        if (AuthenticationMode == OpenAiCompatibleAuthenticationMode.ApiKeyHeader)
        {
            ValidateHeaderName(ApiKeyHeaderName);
        }

        ValidateEnvironmentVariableName(ApiKeyEnvironmentVariable);

        if (MaxCompletionTokens <= 0)
        {
            throw new InvalidOperationException("Max completion tokens must be greater than zero.");
        }

        if (IncludeThinkingConfiguration &&
            !string.Equals(ThinkingMode, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Thinking mode must be 'disabled' when the thinking extension is included.");
        }

        if (FirstByteTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The first-byte timeout must be positive.");
        }

        if (StreamIdleTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The stream idle timeout must be positive.");
        }

        _ = OpenAiCompatibleEndpointBuilder.Build(baseUri, ChatCompletionsPath);
    }

    public Uri GetBaseUri()
    {
        Validate();
        return new Uri(BaseUrl.Trim(), UriKind.Absolute);
    }

    public ProviderCredentialScope CreateCredentialScope()
    {
        Validate();
        return ProviderCredentialScope.Create(
            new Uri(BaseUrl.Trim(), UriKind.Absolute),
            AuthenticationMode,
            AuthenticationMode == OpenAiCompatibleAuthenticationMode.ApiKeyHeader
                ? ApiKeyHeaderName
                : null);
    }

    private static Uri ValidateBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                "The OpenAI-compatible base URL must be an absolute URL.");
        }

        if (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException(
                "The OpenAI-compatible base URL must use HTTP or HTTPS.");
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(baseUri))
        {
            throw new InvalidOperationException(
                "The OpenAI-compatible base URL must use HTTPS unless the host is loopback.");
        }

        if (!string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new InvalidOperationException("The base URL must not contain user information.");
        }

        if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException(
                "The base URL must not contain a query string or fragment.");
        }

        return baseUri;
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static void ValidateHeaderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.All(IsHttpTokenCharacter) ||
            ForbiddenApiKeyHeaders.Contains(value.Trim()))
        {
            throw new InvalidOperationException(
                "The configured API key header name is invalid or forbidden.");
        }
    }

    private static bool IsHttpTokenCharacter(char value)
    {
        return char.IsAsciiLetterOrDigit(value) || value is
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or
            '^' or '_' or '`' or '|' or '~';
    }

    private static void ValidateEnvironmentVariableName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Skip(1).Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new InvalidOperationException(
                "The API key environment variable name is invalid.");
        }
    }
}
