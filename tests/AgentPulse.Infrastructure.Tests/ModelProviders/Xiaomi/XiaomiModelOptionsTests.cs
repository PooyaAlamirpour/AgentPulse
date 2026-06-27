using AgentPulse.Infrastructure.ModelProviders.Xiaomi;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.Xiaomi;

public sealed class XiaomiModelOptionsTests
{
    [Theory]
    [InlineData("https://api.xiaomimimo.com/v1")]
    [InlineData("https://custom-provider.example/v1")]
    [InlineData("http://localhost:5000/v1")]
    [InlineData("http://127.0.0.1:5000/v1")]
    [InlineData("http://[::1]:5000/v1")]
    public void Accepts_https_and_loopback_http(string baseUrl)
    {
        var options = new XiaomiModelOptions { BaseUrl = baseUrl };

        options.Validate();
    }

    [Theory]
    [InlineData("http://example.com/v1")]
    [InlineData("http://192.168.1.20:5000/v1")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///tmp/provider")]
    public void Rejects_non_loopback_http_and_unsupported_schemes(string baseUrl)
    {
        var options = new XiaomiModelOptions { BaseUrl = baseUrl };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.DoesNotContain("api-key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("auto")]
    public void Rejects_unsupported_thinking_modes(string thinkingMode)
    {
        var options = new XiaomiModelOptions { ThinkingMode = thinkingMode };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
