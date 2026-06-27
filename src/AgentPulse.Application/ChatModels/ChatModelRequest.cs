namespace AgentPulse.Application.ChatModels;

public sealed class ChatModelRequest
{
    public ChatModelRequest(IEnumerable<ChatModelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var copiedMessages = messages.ToArray();

        if (copiedMessages.Length == 0)
        {
            throw new ArgumentException(
                "A chat model request must contain at least one message.",
                nameof(messages));
        }

        if (copiedMessages.Any(static message => message is null))
        {
            throw new ArgumentException(
                "A chat model request cannot contain a null message.",
                nameof(messages));
        }

        if (!copiedMessages.Any(static message => message.Role == ChatModelRole.System))
        {
            throw new ArgumentException(
                "A chat model request must contain at least one system message.",
                nameof(messages));
        }

        Messages = Array.AsReadOnly(copiedMessages);
    }

    public IReadOnlyList<ChatModelMessage> Messages { get; }
}
