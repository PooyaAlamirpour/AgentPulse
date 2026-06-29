using System.Text.Json;
using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal static class OpenAiCompatibleChatRequestMapper
{
    public static OpenAiCompatibleChatRequestDto Map(
        ChatModelRequest request,
        OpenAiCompatibleModelOptions options,
        bool stream = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var messages = request.Messages.Select(MapMessage).ToArray();
        var thinking = options.IncludeThinkingConfiguration
            ? new OpenAiCompatibleThinkingDto(options.ThinkingMode.Trim().ToLowerInvariant())
            : null;
        var tools = request.Tools.Count == 0
            ? null
            : request.Tools.Select(static tool => new OpenAiCompatibleToolDto(
                "function",
                new OpenAiCompatibleFunctionDefinitionDto(
                    tool.Name,
                    tool.Description,
                    ParseSchema(tool.ParametersJsonSchema)))).ToArray();

        return new OpenAiCompatibleChatRequestDto(
            request.Model ?? options.Model.Trim(),
            messages,
            stream,
            stream ? new OpenAiCompatibleStreamOptionsDto(IncludeUsage: true) : null,
            options.MaxCompletionTokens,
            thinking,
            tools,
            tools is null ? null : "auto");
    }

    private static OpenAiCompatibleChatMessageDto MapMessage(ChatModelMessage message)
    {
        var toolCalls = message.ToolCalls.Count == 0
            ? null
            : message.ToolCalls
                .OrderBy(static call => call.Order)
                .Select(static call => new OpenAiCompatibleToolCallDto(
                    call.Id,
                    "function",
                    new OpenAiCompatibleFunctionCallDto(call.Name, call.ArgumentsJson)))
                .ToArray();

        return new OpenAiCompatibleChatMessageDto(
            MapRole(message.Role),
            message.Content,
            toolCalls,
            message.ToolCallId,
            message.ToolName);
    }

    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }

    private static string MapRole(ChatModelRole role)
    {
        return role switch
        {
            ChatModelRole.System => "system",
            ChatModelRole.User => "user",
            ChatModelRole.Assistant => "assistant",
            ChatModelRole.Tool => "tool",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown chat model role."),
        };
    }
}
