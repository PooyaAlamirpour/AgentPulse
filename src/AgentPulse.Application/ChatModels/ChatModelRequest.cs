namespace AgentPulse.Application.ChatModels;

public sealed class ChatModelRequest
{
    public ChatModelRequest(
        IEnumerable<ChatModelMessage> messages,
        string? model = null,
        IEnumerable<ChatModelToolDefinition>? tools = null)
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

        var copiedTools = (tools ?? []).ToArray();
        if (copiedTools.Select(static tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != copiedTools.Length)
        {
            throw new ArgumentException("Tool definition names must be unique.", nameof(tools));
        }

        if (model is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            Model = model.Trim();
        }

        Messages = Array.AsReadOnly(copiedMessages);
        Tools = Array.AsReadOnly(copiedTools);
    }

    public IReadOnlyList<ChatModelMessage> Messages { get; }

    public IReadOnlyList<ChatModelToolDefinition> Tools { get; }

    public string? Model { get; }
}
