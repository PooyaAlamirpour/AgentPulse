using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.Xiaomi;

internal static class XiaomiChatRequestMapper
{
    public static XiaomiChatRequestDto Map(
        ChatModelRequest request,
        XiaomiModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var messages = request.Messages
            .Select(static message => new XiaomiChatMessageDto(
                MapRole(message.Role),
                message.Content))
            .ToArray();

        return new XiaomiChatRequestDto(
            options.Model.Trim(),
            messages,
            Stream: true,
            options.MaxCompletionTokens,
            new XiaomiThinkingDto(options.ThinkingMode.Trim().ToLowerInvariant()));
    }

    private static string MapRole(ChatModelRole role)
    {
        return role switch
        {
            ChatModelRole.System => "system",
            ChatModelRole.User => "user",
            ChatModelRole.Assistant => "assistant",
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown chat model role."),
        };
    }
}
