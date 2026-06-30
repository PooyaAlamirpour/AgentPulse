using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentLoop;

internal sealed class RepeatedToolFailureDetector
{
    internal const int RepetitionThreshold = 2;

    private List<FailureObservation> _previousTurn = [];
    private readonly List<FailureObservation> _currentTurn = [];
    private int _identicalTurnCount;
    private bool _turnContainsSuccess;

    public void Observe(AgentLoopToolExecution execution)
    {
        if (execution.Result.Succeeded)
        {
            _turnContainsSuccess = true;
            return;
        }

        var callFingerprint = CreateCallFingerprint(execution.Call);
        var failureFingerprint = CreateFailureFingerprint(callFingerprint, execution.Result);
        _currentTurn.Add(new FailureObservation(
            execution.Call.Name,
            execution.Result.Error ?? "The tool failed without a stable reason.",
            failureFingerprint,
            execution.Result.FailureClassification));
    }

    public RepeatedToolFailure? CompleteTurn()
    {
        if (_turnContainsSuccess)
        {
            Reset();
            return null;
        }

        var repeatedFailure = FindRepeatedFailureWithinCurrentTurn();
        var identicalToPreviousTurn = AreIdentical(_previousTurn, _currentTurn);
        _identicalTurnCount = identicalToPreviousTurn
            ? _identicalTurnCount + 1
            : 1;

        if (repeatedFailure is null &&
            _identicalTurnCount >= RepetitionThreshold)
        {
            repeatedFailure = _currentTurn
                .Where(static observation =>
                    observation.Classification == AgentToolFailureClassification.Deterministic)
                .Select(observation => new RepeatedToolFailure(
                    observation.ToolName,
                    observation.Reason,
                    observation.FailureFingerprint,
                    _identicalTurnCount))
                .FirstOrDefault();
        }

        _previousTurn = [.. _currentTurn];
        _currentTurn.Clear();
        _turnContainsSuccess = false;
        return repeatedFailure;
    }

    private RepeatedToolFailure? FindRepeatedFailureWithinCurrentTurn()
    {
        FailureObservation? previous = null;
        var count = 0;
        foreach (var observation in _currentTurn)
        {
            if (previous is not null &&
                string.Equals(
                    previous.FailureFingerprint,
                    observation.FailureFingerprint,
                    StringComparison.Ordinal))
            {
                count++;
            }
            else
            {
                previous = observation;
                count = 1;
            }

            if (count >= RepetitionThreshold &&
                observation.Classification == AgentToolFailureClassification.Deterministic)
            {
                return new RepeatedToolFailure(
                    observation.ToolName,
                    observation.Reason,
                    observation.FailureFingerprint,
                    count);
            }
        }

        return null;
    }

    private static bool AreIdentical(
        IReadOnlyList<FailureObservation> previous,
        IReadOnlyList<FailureObservation> current)
    {
        if (previous.Count == 0 || previous.Count != current.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            if (!string.Equals(
                    previous[index].FailureFingerprint,
                    current[index].FailureFingerprint,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void Reset()
    {
        _previousTurn.Clear();
        _currentTurn.Clear();
        _identicalTurnCount = 0;
        _turnContainsSuccess = false;
    }

    private static string CreateCallFingerprint(ChatModelToolCall call)
    {
        var canonicalArguments = CanonicalizeArguments(call.ArgumentsJson);
        return Hash($"{call.Name}\n{canonicalArguments}");
    }

    private static string CreateFailureFingerprint(
        string callFingerprint,
        AgentToolResult result)
    {
        var reason = NormalizeFailureReason(result.Error);
        return Hash($"{callFingerprint}\n{result.FailureClassification}\n{reason}");
    }

    private static string CanonicalizeArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return argumentsJson.Trim();
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
        }
    }

    private static string NormalizeFailureReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : reason.Trim().Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record FailureObservation(
        string ToolName,
        string Reason,
        string FailureFingerprint,
        AgentToolFailureClassification Classification);
}

internal sealed record RepeatedToolFailure(
    string ToolName,
    string Reason,
    string Fingerprint,
    int Count);
