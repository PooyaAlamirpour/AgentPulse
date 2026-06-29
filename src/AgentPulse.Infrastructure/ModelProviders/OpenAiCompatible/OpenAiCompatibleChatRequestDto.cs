using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal sealed record OpenAiCompatibleChatRequestDto(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiCompatibleChatMessageDto> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("stream_options")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenAiCompatibleStreamOptionsDto? StreamOptions,
    [property: JsonPropertyName("max_completion_tokens")] int MaxCompletionTokens,
    [property: JsonPropertyName("thinking")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenAiCompatibleThinkingDto? Thinking,
    [property: JsonPropertyName("tools")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OpenAiCompatibleToolDto>? Tools,
    [property: JsonPropertyName("tool_choice")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolChoice);

internal sealed record OpenAiCompatibleChatMessageDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Content,
    [property: JsonPropertyName("tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OpenAiCompatibleToolCallDto>? ToolCalls,
    [property: JsonPropertyName("tool_call_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolCallId,
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name);

internal sealed record OpenAiCompatibleToolCallDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAiCompatibleFunctionCallDto Function);

internal sealed record OpenAiCompatibleFunctionCallDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

internal sealed record OpenAiCompatibleToolDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAiCompatibleFunctionDefinitionDto Function);

internal sealed record OpenAiCompatibleFunctionDefinitionDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

internal sealed record OpenAiCompatibleStreamOptionsDto(
    [property: JsonPropertyName("include_usage")] bool IncludeUsage);

internal sealed record OpenAiCompatibleThinkingDto(
    [property: JsonPropertyName("type")] string Type);
