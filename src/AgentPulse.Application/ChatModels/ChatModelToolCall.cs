namespace AgentPulse.Application.ChatModels;

public sealed record ChatModelToolCall
{
    public ChatModelToolCall(string id, string name, string argumentsJson, int order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(argumentsJson);
        if (order <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Tool call order must be positive.");
        }

        Id = id.Trim();
        Name = name.Trim();
        ArgumentsJson = argumentsJson;
        Order = order;
    }

    public string Id { get; }

    public string Name { get; }

    public string ArgumentsJson { get; }

    public int Order { get; }
}
