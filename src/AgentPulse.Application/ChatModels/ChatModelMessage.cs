using System.Text.Json.Serialization;

namespace AgentPulse.Application.ChatModels;

public sealed class ChatModelMessage
{
    public ChatModelMessage(ChatModelRole role, string content)
        : this(role, content, [], null, null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
    }

    [JsonConstructor]
    public ChatModelMessage(
        ChatModelRole role,
        string? content,
        IReadOnlyList<ChatModelToolCall>? toolCalls,
        string? toolCallId,
        string? toolName)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown chat model role.");
        }

        Role = role;
        Content = content;
        ToolCalls = Array.AsReadOnly((toolCalls ?? []).OrderBy(static call => call.Order).ToArray());
        ToolCallId = toolCallId;
        ToolName = toolName;
    }

    public ChatModelRole Role { get; }
    public string? Content { get; }
    public IReadOnlyList<ChatModelToolCall> ToolCalls { get; }
    public string? ToolCallId { get; }
    public string? ToolName { get; }

    public static ChatModelMessage CreateAssistantToolCalls(string? content, IEnumerable<ChatModelToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        var copiedCalls = toolCalls.OrderBy(static call => call.Order).ToArray();
        if (copiedCalls.Length == 0)
        {
            throw new ArgumentException("At least one tool call is required.", nameof(toolCalls));
        }

        return new ChatModelMessage(ChatModelRole.Assistant, content, copiedCalls, null, null);
    }

    public static ChatModelMessage CreateToolResult(string toolCallId, string toolName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(content);
        return new ChatModelMessage(ChatModelRole.Tool, content, [], toolCallId.Trim(), toolName.Trim());
    }
}
