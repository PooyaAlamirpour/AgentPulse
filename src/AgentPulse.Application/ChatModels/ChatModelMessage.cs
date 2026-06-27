namespace AgentPulse.Application.ChatModels;

public sealed class ChatModelMessage
{
    public ChatModelMessage(ChatModelRole role, string content)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown chat model role.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        Role = role;
        Content = content;
    }

    public ChatModelRole Role { get; }

    public string Content { get; }
}
