using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.ModelRuns;

public sealed record AssistantCompletionMetadata
{
    public AssistantCompletionMetadata(
        string model,
        ModelFinishReason finishReason,
        ModelUsage? usage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (!Enum.IsDefined(finishReason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finishReason),
                finishReason,
                "Unknown model finish reason.");
        }

        Model = model.Trim();
        FinishReason = finishReason;
        Usage = usage;
    }

    public string Model { get; }

    public ModelFinishReason FinishReason { get; }

    public ModelUsage? Usage { get; }
}
