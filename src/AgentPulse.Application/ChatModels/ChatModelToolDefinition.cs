using System.Text.Json;

namespace AgentPulse.Application.ChatModels;

public sealed record ChatModelToolDefinition
{
    public ChatModelToolDefinition(string name, string description, string parametersJsonSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersJsonSchema);

        using var schema = JsonDocument.Parse(parametersJsonSchema);
        if (schema.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Tool parameters schema must be a JSON object.",
                nameof(parametersJsonSchema));
        }

        Name = name.Trim();
        Description = description.Trim();
        ParametersJsonSchema = schema.RootElement.GetRawText();
    }

    public string Name { get; }

    public string Description { get; }

    public string ParametersJsonSchema { get; }
}
