namespace AgentPulse.Application.ChatModels;

public sealed record ModelUsage
{
    public ModelUsage(long inputTokens, long outputTokens, long totalTokens)
    {
        if (inputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputTokens),
                inputTokens,
                "Input token count cannot be negative.");
        }

        if (outputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputTokens),
                outputTokens,
                "Output token count cannot be negative.");
        }

        if (totalTokens < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalTokens),
                totalTokens,
                "Total token count cannot be negative.");
        }

        long expectedTotal;
        try
        {
            expectedTotal = checked(inputTokens + outputTokens);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalTokens),
                totalTokens,
                "Input and output token counts exceed the supported range.");
        }

        if (totalTokens != expectedTotal)
        {
            throw new ArgumentException(
                "Total token count must equal input tokens plus output tokens.",
                nameof(totalTokens));
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
    }

    public long InputTokens { get; }

    public long OutputTokens { get; }

    public long TotalTokens { get; }
}
