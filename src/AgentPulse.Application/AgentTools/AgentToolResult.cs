namespace AgentPulse.Application.AgentTools;

public sealed record AgentToolResult
{
    private AgentToolResult(
        bool succeeded,
        string output,
        string? error,
        IReadOnlyDictionary<string, string> metadata,
        AgentToolFailureClassification failureClassification)
    {
        Succeeded = succeeded;
        Output = output;
        Error = error;
        Metadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(metadata, StringComparer.Ordinal));
        FailureClassification = failureClassification;
    }

    public bool Succeeded { get; }

    public string Output { get; }

    public string? Error { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public AgentToolFailureClassification FailureClassification { get; }

    public static AgentToolResult Success(
        string output,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new AgentToolResult(
            true,
            output,
            null,
            metadata ?? new Dictionary<string, string>(),
            AgentToolFailureClassification.None);
    }

    public static AgentToolResult Failure(
        string error,
        string output = "",
        IReadOnlyDictionary<string, string>? metadata = null,
        AgentToolFailureClassification classification = AgentToolFailureClassification.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ArgumentNullException.ThrowIfNull(output);
        if (!Enum.IsDefined(classification) ||
            classification == AgentToolFailureClassification.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(classification),
                classification,
                "A failed tool result must have a defined non-success classification.");
        }

        return new AgentToolResult(
            false,
            output,
            error.Trim(),
            metadata ?? new Dictionary<string, string>(),
            classification);
    }
}
