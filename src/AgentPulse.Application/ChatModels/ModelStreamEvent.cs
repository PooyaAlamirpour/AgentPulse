namespace AgentPulse.Application.ChatModels;

public abstract record ModelStreamEvent
{
    private ModelStreamEvent()
    {
    }

    public sealed record TextDelta : ModelStreamEvent
    {
        public TextDelta(string text)
        {
            ArgumentException.ThrowIfNullOrEmpty(text);
            Text = text;
        }

        public string Text { get; }
    }

    public sealed record Completed : ModelStreamEvent
    {
        public Completed(ModelFinishReason finishReason)
        {
            if (!Enum.IsDefined(finishReason))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(finishReason),
                    finishReason,
                    "Unknown model finish reason.");
            }

            FinishReason = finishReason;
        }

        public ModelFinishReason FinishReason { get; }
    }

    public sealed record Failed : ModelStreamEvent
    {
        public Failed(string errorMessage)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
            ErrorMessage = errorMessage;
        }

        public string ErrorMessage { get; }
    }

    public sealed record Usage : ModelStreamEvent
    {
        public Usage(ModelUsage value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        public ModelUsage Value { get; }
    }
}
