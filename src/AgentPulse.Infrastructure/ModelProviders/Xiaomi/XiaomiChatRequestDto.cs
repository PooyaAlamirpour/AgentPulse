using System.Text.Json.Serialization;

namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

internal sealed record XiaomiChatRequestDto(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<XiaomiChatMessageDto> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("max_completion_tokens")] int MaxCompletionTokens,
    [property: JsonPropertyName("thinking")] XiaomiThinkingDto Thinking);

internal sealed record XiaomiChatMessageDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record XiaomiThinkingDto(
    [property: JsonPropertyName("type")] string Type);
