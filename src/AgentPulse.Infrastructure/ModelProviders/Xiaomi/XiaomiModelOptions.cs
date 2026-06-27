namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

public sealed class XiaomiModelOptions
{
    public const string SectionName = "AgentPulse:Xiaomi";

    public string BaseUrl { get; set; } = "https://api.xiaomimimo.com/v1";

    public string Model { get; set; } = "mimo-v2.5-pro";

    public int MaxCompletionTokens { get; set; } = 4096;

    public string ThinkingMode { get; set; } = "disabled";

    public TimeSpan FirstByteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                "Xiaomi MiMo base URL must be an absolute HTTP or HTTPS URL.");
        }

        if (baseUri.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(baseUri))
        {
            throw new InvalidOperationException(
                "Xiaomi MiMo base URL must use HTTPS unless the host is localhost or a loopback address.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("Xiaomi MiMo model name must not be empty.");
        }

        if (MaxCompletionTokens <= 0)
        {
            throw new InvalidOperationException(
                "Xiaomi MiMo max completion tokens must be greater than zero.");
        }

        if (!string.Equals(ThinkingMode, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Xiaomi MiMo thinking mode must be 'disabled' in this phase.");
        }

        if (FirstByteTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Xiaomi MiMo first-byte timeout must be positive.");
        }

        if (StreamIdleTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Xiaomi MiMo stream idle timeout must be positive.");
        }
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(uri.Host, out var address) &&
               System.Net.IPAddress.IsLoopback(address);
    }
}
