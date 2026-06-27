using AgentPulse.Application.ChatModels;

namespace AgentPulse.Infrastructure.ModelProviders.OpenAiCompatible;

internal static class OpenAiCompatibleChatRequestMapper
{
    public static OpenAiCompatibleChatRequestDto Map(
        ChatModelRequest request,
        OpenAiCompatibleModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var messages = request.Messages
            .Select(static message => new OpenAiCompatibleChatMessageDto(
                MapRole(message.Role),
                message.Content))
            .ToArray();
        var thinking = options.IncludeThinkingConfiguration
            ? new OpenAiCompatibleThinkingDto(
                options.ThinkingMode.Trim().ToLowerInvariant())
            : null;

        return new OpenAiCompatibleChatRequestDto(
            request.Model ?? options.Model.Trim(),
            messages,
            Stream: true,
            new OpenAiCompatibleStreamOptionsDto(IncludeUsage: true),
            options.MaxCompletionTokens,
            thinking);
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
