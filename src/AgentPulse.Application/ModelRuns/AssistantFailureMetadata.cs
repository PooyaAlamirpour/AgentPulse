namespace AgentPulse.Application.ModelRuns;

public sealed record AssistantFailureMetadata
{
    public AssistantFailureMetadata(
        string model,
        string reason,
        string kind,
        string? stage = null,
        int? statusCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "Failure status code must be a valid HTTP status code.");
        }

        Model = model.Trim();
        Reason = reason.Trim();
        Kind = kind.Trim();
        Stage = string.IsNullOrWhiteSpace(stage) ? null : stage.Trim();
        StatusCode = statusCode;
    }

    public string Model { get; }

    public string Reason { get; }

    public string Kind { get; }

    public string? Stage { get; }

    public int? StatusCode { get; }
}
