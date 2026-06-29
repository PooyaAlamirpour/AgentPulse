namespace AgentPulse.Domain.Messages;

public sealed class ToolCallMessagePart : MessagePart
{
    private const int MaximumToolCallIdLength = 256;
    private const int MaximumToolNameLength = 128;

    private ToolCallMessagePart()
    {
        ToolCallId = null!;
        ToolName = null!;
        ArgumentsJson = null!;
    }

    internal ToolCallMessagePart(
        MessagePartId id,
        MessageId messageId,
        int order,
        string toolCallId,
        string toolName,
        string argumentsJson,
        DateTime createdAtUtc)
        : base(id, messageId, order, createdAtUtc)
    {
        ToolCallId = Normalize(toolCallId, MaximumToolCallIdLength, nameof(toolCallId));
        ToolName = Normalize(toolName, MaximumToolNameLength, nameof(toolName));
        ArgumentNullException.ThrowIfNull(argumentsJson);
        ArgumentsJson = argumentsJson;
    }

    public string ToolCallId { get; private set; }

    public string ToolName { get; private set; }

    public string ArgumentsJson { get; private set; }

    private static string Normalize(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }
}
