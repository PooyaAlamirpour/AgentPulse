using AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

public sealed class XiaomiModelOptions
{
    public const string SectionName = "AgentPulse:Xiaomi";

    public string BaseUrl { get; set; } = OpenAiCompatibleModelOptions.XiaomiDefaultBaseUrl;

    public string Model { get; set; } = OpenAiCompatibleModelOptions.XiaomiDefaultModel;

    public int MaxCompletionTokens { get; set; } = 4096;

    public string ThinkingMode { get; set; } = "disabled";

    public TimeSpan FirstByteTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        ToOpenAiCompatibleOptions().Validate();
    }

    internal OpenAiCompatibleModelOptions ToOpenAiCompatibleOptions()
    {
        return new OpenAiCompatibleModelOptions
        {
            BaseUrl = BaseUrl,
            ChatCompletionsPath = "chat/completions",
            Model = Model,
            AuthenticationMode = OpenAiCompatibleAuthenticationMode.ApiKeyHeader,
            ApiKeyHeaderName = "api-key",
            ApiKeyEnvironmentVariable =
                OpenAiCompatibleModelOptions.XiaomiDefaultApiKeyEnvironmentVariable,
            MaxCompletionTokens = MaxCompletionTokens,
            ThinkingMode = ThinkingMode,
            IncludeThinkingConfiguration = true,
            FirstByteTimeout = FirstByteTimeout,
            StreamIdleTimeout = StreamIdleTimeout,
        };
    }
}
