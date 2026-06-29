namespace AgentPulse.Application.ChatModels;

public sealed record ChatModelResponse
{
    public ChatModelResponse(
        string? text,
        IEnumerable<ChatModelToolCall>? toolCalls,
        ModelFinishReason finishReason,
        ModelUsage? usage = null)
    {
        if (!Enum.IsDefined(finishReason))
        {
            throw new ArgumentOutOfRangeException(nameof(finishReason), finishReason, "Unknown model finish reason.");
        }

        var copiedCalls = (toolCalls ?? []).OrderBy(static call => call.Order).ToArray();
        if (copiedCalls.Any(static call => call is null))
        {
            throw new ArgumentException("Tool calls cannot contain null values.", nameof(toolCalls));
        }

        if (copiedCalls.Select(static call => call.Id).Distinct(StringComparer.Ordinal).Count() != copiedCalls.Length)
        {
            throw new ArgumentException("Tool call identifiers must be unique.", nameof(toolCalls));
        }

        Text = text;
        ToolCalls = Array.AsReadOnly(copiedCalls);
        FinishReason = finishReason;
        Usage = usage;
    }

    public string? Text { get; }

    public IReadOnlyList<ChatModelToolCall> ToolCalls { get; }

    public ModelFinishReason FinishReason { get; }

    public ModelUsage? Usage { get; }
}
