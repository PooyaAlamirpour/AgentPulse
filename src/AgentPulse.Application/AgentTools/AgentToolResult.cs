namespace AgentPulse.Application.AgentTools;

public sealed record AgentToolResult
{
    private AgentToolResult(
        bool succeeded,
        string output,
        string? error,
        IReadOnlyDictionary<string, string> metadata)
    {
        Succeeded = succeeded;
        Output = output;
        Error = error;
        Metadata = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    public bool Succeeded { get; }

    public string Output { get; }

    public string? Error { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public static AgentToolResult Success(
        string output,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new AgentToolResult(
            true,
            output,
            null,
            metadata ?? new Dictionary<string, string>());
    }

    public static AgentToolResult Failure(
        string error,
        string output = "",
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ArgumentNullException.ThrowIfNull(output);
        return new AgentToolResult(
            false,
            output,
            error.Trim(),
            metadata ?? new Dictionary<string, string>());
    }
}
