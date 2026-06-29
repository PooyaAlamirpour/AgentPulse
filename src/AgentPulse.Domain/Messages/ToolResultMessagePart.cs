using AgentPulse.Domain.Sessions;

namespace AgentPulse.Domain.Messages;

public sealed class ToolResultMessagePart : MessagePart
{
    private const int MaximumToolCallIdLength = 256;
    private const int MaximumToolNameLength = 128;
    private const int MaximumErrorLength = 4096;

    private ToolResultMessagePart()
    {
        ToolCallId = null!;
        ToolName = null!;
        Output = null!;
    }

    internal ToolResultMessagePart(
        MessagePartId id,
        MessageId messageId,
        SessionId sessionId,
        MessageId assistantToolCallMessageId,
        int order,
        string toolCallId,
        string toolName,
        bool succeeded,
        string output,
        string? error,
        string? metadataJson,
        DateTime createdAtUtc)
        : base(id, messageId, order, createdAtUtc)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Session identifier cannot be empty.", nameof(sessionId));
        }

        if (assistantToolCallMessageId.Value == Guid.Empty)
        {
            throw new ArgumentException("Assistant tool-call message identifier cannot be empty.", nameof(assistantToolCallMessageId));
        }

        SessionId = sessionId;
        AssistantToolCallMessageId = assistantToolCallMessageId;
        ToolCallId = NormalizeRequired(toolCallId, MaximumToolCallIdLength, nameof(toolCallId));
        ToolName = NormalizeRequired(toolName, MaximumToolNameLength, nameof(toolName));
        ArgumentNullException.ThrowIfNull(output);
        Output = output;
        Error = NormalizeOptional(error, MaximumErrorLength, nameof(error));
        MetadataJson = string.IsNullOrWhiteSpace(metadataJson) ? null : metadataJson;
        Succeeded = succeeded;

        if (succeeded && Error is not null)
        {
            throw new ArgumentException(
                "A successful tool result cannot contain an error message.",
                nameof(error));
        }
    }

    public SessionId SessionId { get; private set; }

    public MessageId AssistantToolCallMessageId { get; private set; }

    public string ToolCallId { get; private set; }

    public string ToolName { get; private set; }

    public bool Succeeded { get; private set; }

    public string Output { get; private set; }

    public string? Error { get; private set; }

    public string? MetadataJson { get; private set; }

    private static string NormalizeRequired(
        string value,
        int maximumLength,
        string parameterName)
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

    private static string? NormalizeOptional(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return NormalizeRequired(value, maximumLength, parameterName);
    }
}
