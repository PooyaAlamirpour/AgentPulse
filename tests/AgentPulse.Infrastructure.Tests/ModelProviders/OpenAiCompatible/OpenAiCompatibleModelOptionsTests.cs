using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.Tests.ModelProviders.OpenAiCompatible;

public sealed class OpenAiCompatibleModelOptionsTests
{
    [Fact]
    public void Defaults_use_the_generic_openai_compatible_profile()
    {
        var options = new OpenAiCompatibleModelOptions();

        options.Validate();

        Assert.Equal("https://api.openai.com/v1", options.BaseUrl);
        Assert.Equal("chat/completions", options.ChatCompletionsPath);
        Assert.Equal("gpt-4.1-mini", options.Model);
        Assert.Equal(OpenAiCompatibleAuthenticationMode.Bearer, options.AuthenticationMode);
        Assert.Equal("api-key", options.ApiKeyHeaderName);
        Assert.Equal("OPENAI_API_KEY", options.ApiKeyEnvironmentVariable);
        Assert.Equal(4096, options.MaxCompletionTokens);
        Assert.Equal("disabled", options.ThinkingMode);
        Assert.False(options.IncludeThinkingConfiguration);
        Assert.Equal(TimeSpan.FromSeconds(30), options.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), options.StreamIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ErrorBodyReadTimeout);
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
    [InlineData("chat/%2e%2e%2fadmin")]
    [InlineData("chat/%2e%2e%5cadmin")]
    [InlineData("chat/%252e%252e%252fadmin")]
    [InlineData("chat/%00/completions")]
    [InlineData("chat/completions%3ftoken=x")]
    [InlineData("chat/completions%23fragment")]
    [InlineData("/chat/completions")]
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
    [InlineData("host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("Content-Type")]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("bad:header")]
    [InlineData("bad\r\nheader")]
    public void Rejects_invalid_or_sensitive_api_key_headers(string headerName)
    {
        var options = new OpenAiCompatibleModelOptions
        {
            AuthenticationMode = OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            ApiKeyHeaderName = headerName,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("x-provider-key")]
    public void Accepts_valid_custom_api_key_headers(string headerName)
    {
        var options = new OpenAiCompatibleModelOptions
        {
            AuthenticationMode = OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            ApiKeyHeaderName = headerName,
        };

        options.Validate();
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
        Assert.Throws<InvalidOperationException>(
            new OpenAiCompatibleModelOptions { ErrorBodyReadTimeout = TimeSpan.Zero }.Validate);
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

    [Theory]
    [InlineData("chat/completions")]
    [InlineData("v1/chat/completions")]
    [InlineData("api/models/chat-completions")]
    public void Accepts_safe_relative_chat_completion_paths(string path)
    {
        var options = new OpenAiCompatibleModelOptions
        {
            ChatCompletionsPath = path,
        };

        options.Validate();
    }

    [Fact]
    public void Endpoint_join_preserves_base_path_and_normalizes_slashes()
    {
        var endpoint = OpenAiCompatibleEndpointBuilder.Build(
            "https://provider.example/v1//",
            "chat//completions/");

        Assert.Equal("https://provider.example/v1/chat/completions", endpoint.ToString().TrimEnd('/'));
    }
}
