using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPulse.Infrastructure.AgentTools;

internal static class ToolArgumentReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T Deserialize<T>(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        }

        try
        {
            return arguments.Deserialize<T>(Options)
                ?? throw new JsonException("Tool arguments cannot be null.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ArgumentException($"Tool arguments are invalid: {exception.Message}", exception);
        }
    }
}
