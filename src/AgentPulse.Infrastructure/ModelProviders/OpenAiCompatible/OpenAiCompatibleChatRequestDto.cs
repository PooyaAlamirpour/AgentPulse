using System.Text.Json.Serialization;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal sealed record OpenAiCompatibleChatRequestDto(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiCompatibleChatMessageDto> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("stream_options")] OpenAiCompatibleStreamOptionsDto StreamOptions,
    [property: JsonPropertyName("max_completion_tokens")] int MaxCompletionTokens,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenAiCompatibleThinkingDto? Thinking);

internal sealed record OpenAiCompatibleChatMessageDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OpenAiCompatibleStreamOptionsDto(
    [property: JsonPropertyName("include_usage")] bool IncludeUsage);

internal sealed record OpenAiCompatibleThinkingDto(
    [property: JsonPropertyName("type")] string Type);
