using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.OpenAiCompatible;

public sealed class OpenAiCompatibleModelOptionsTests
{
    [Fact]
    public void Defaults_preserve_the_xiaomi_profile()
    {
        var options = new OpenAiCompatibleModelOptions();

        options.Validate();

        Assert.Equal("https://api.xiaomimimo.com/v1", options.BaseUrl);
        Assert.Equal("chat/completions", options.ChatCompletionsPath);
        Assert.Equal("mimo-v2.5-pro", options.Model);
        Assert.Equal(OpenAiCompatibleAuthenticationMode.ApiKeyHeader, options.AuthenticationMode);
        Assert.Equal("api-key", options.ApiKeyHeaderName);
        Assert.Equal("MIMO_API_KEY", options.ApiKeyEnvironmentVariable);
        Assert.Equal(4096, options.MaxCompletionTokens);
        Assert.Equal("disabled", options.ThinkingMode);
        Assert.True(options.IncludeThinkingConfiguration);
        Assert.Equal(TimeSpan.FromSeconds(30), options.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), options.StreamIdleTimeout);
        Assert.DoesNotContain(
            typeof(OpenAiCompatibleModelOptions).GetProperties(),
            property => string.Equals(property.Name, "ApiKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://provider.example/v1")]
    [InlineData("http://localhost:5000/v1")]
    [InlineData("http://127.0.0.1:5000/v1")]
    [InlineData("http://[::1]:5000/v1")]
    public void Accepts_https_and_loopback_http(string baseUrl)
    {
        var options = new OpenAiCompatibleModelOptions { BaseUrl = baseUrl };

        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/v1")]
    [InlineData("http://provider.example/v1")]
    [InlineData("http://192.168.1.10:5000/v1")]
    [InlineData("ftp://provider.example/v1")]
    [InlineData("file:///tmp/provider")]
    [InlineData("https://provider.example/v1?secret=value")]
    [InlineData("https://provider.example/v1#fragment")]
    public void Rejects_invalid_base_urls(string baseUrl)
    {
        var options = new OpenAiCompatibleModelOptions { BaseUrl = baseUrl };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://other.example/chat/completions")]
    [InlineData("//other.example/chat/completions")]
    [InlineData("file:///tmp/file")]
    [InlineData("../chat/completions")]
    [InlineData("chat/../completions")]
    [InlineData("chat/%2e%2e/completions")]
    [InlineData("chat/completions?tenant=1")]
    [InlineData("chat/completions#fragment")]
    public void Rejects_unsafe_chat_completion_paths(string path)
    {
        var options = new OpenAiCompatibleModelOptions { ChatCompletionsPath = path };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("api key")]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("bad:header")]
    public void Rejects_invalid_or_sensitive_api_key_headers(string headerName)
    {
        var options = new OpenAiCompatibleModelOptions
        {
            ApiKeyHeaderName = headerName,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Bearer_mode_ignores_api_key_header_name()
    {
        var options = new OpenAiCompatibleModelOptions
        {
            AuthenticationMode = OpenAiCompatibleAuthenticationMode.Bearer,
            ApiKeyHeaderName = "Host",
            IncludeThinkingConfiguration = false,
        };

        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("9KEY")]
    [InlineData("API-KEY")]
    [InlineData("API KEY")]
    public void Rejects_invalid_environment_variable_names(string name)
    {
        var options = new OpenAiCompatibleModelOptions
        {
            ApiKeyEnvironmentVariable = name,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Rejects_unknown_authentication_mode()
    {
        var options = new OpenAiCompatibleModelOptions
        {
            AuthenticationMode = (OpenAiCompatibleAuthenticationMode)999,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Rejects_empty_model_name()
    {
        var options = new OpenAiCompatibleModelOptions { Model = " " };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Rejects_unsupported_thinking_mode_when_extension_is_enabled()
    {
        var options = new OpenAiCompatibleModelOptions
        {
            ThinkingMode = "enabled",
            IncludeThinkingConfiguration = true,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_completion_tokens(int value)
    {
        var options = new OpenAiCompatibleModelOptions { MaxCompletionTokens = value };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Rejects_non_positive_timeouts()
    {
        Assert.Throws<InvalidOperationException>(
            new OpenAiCompatibleModelOptions { FirstByteTimeout = TimeSpan.Zero }.Validate);
        Assert.Throws<InvalidOperationException>(
            new OpenAiCompatibleModelOptions { StreamIdleTimeout = TimeSpan.Zero }.Validate);
    }

    [Fact]
    public void Thinking_extension_can_be_omitted_for_generic_endpoints()
    {
        var options = new OpenAiCompatibleModelOptions
        {
            IncludeThinkingConfiguration = false,
            ThinkingMode = "not-sent",
        };

        options.Validate();
    }

    [Fact]
    public void Endpoint_join_preserves_base_path_and_normalizes_slashes()
    {
        var endpoint = OpenAiCompatibleEndpointBuilder.Build(
            "https://provider.example/v1//",
            "/chat//completions/");

        Assert.Equal("https://provider.example/v1/chat/completions", endpoint.ToString().TrimEnd('/'));
    }
}
