namespace AgentPulse.Application.AgentTools;

public sealed record AgentToolResultLimitOutcome(
    AgentToolResult Result,
    bool WasLimited,
    bool OutputWasTruncated,
    bool MetadataWasTruncated);

public static class AgentToolResultLimiter
{
    private const string ToolResultTruncationMessage =
        "[Tool result truncated because it exceeded the configured output limit.]";
    private const string DiffTruncationMessage =
        "[Diff truncated because the tool result exceeded the configured output limit.]";
    private const string MetadataTruncationMessage = "[truncated]";

    private static readonly HashSet<string> ImportantMetadataKeys = new(StringComparer.Ordinal)
    {
        "path",
        "paths",
        "operation",
        "created",
        "modified",
        "deleted",
        "moved",
        "additions",
        "deletions",
        "diffTruncated",
        "diffCharacterCount",
        "sha256Before",
        "sha256After",
        "sha256BeforeByPath",
        "sha256AfterByPath",
        "truncated",
    };

    public static AgentToolResultLimitOutcome Limit(
        AgentToolResult result,
        int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (maximumCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters),
                maximumCharacters,
                "The tool result output limit must be greater than zero.");
        }

        if (CalculateSize(result.Output, result.Error, result.Metadata) <= maximumCharacters)
        {
            return new AgentToolResultLimitOutcome(result, false, false, false);
        }

        var output = result.Output;
        var error = result.Error;
        var metadata = new Dictionary<string, string>(result.Metadata, StringComparer.Ordinal)
        {
            ["truncated"] = "true",
        };
        var outputWasTruncated = false;
        var metadataWasTruncated = false;
        var representsDiff = metadata.ContainsKey("diff") ||
                             metadata.ContainsKey("diffCharacterCount");

        if (metadata.TryGetValue("diff", out var diff))
        {
            metadata["diffCharacterCount"] = diff.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            metadata["diffTruncated"] = "true";
            metadata["diff"] = DiffTruncationMessage;
            metadataWasTruncated = true;
        }

        var suffix = representsDiff
            ? $"\n\n{DiffTruncationMessage}"
            : $"\n\n{ToolResultTruncationMessage}";
        var nonOutputSize = CalculateSize(string.Empty, error, metadata);
        var outputBudget = Math.Max(0, maximumCharacters - nonOutputSize);
        if (output.Length > outputBudget)
        {
            output = TruncateWithSuffix(output, outputBudget, suffix);
            outputWasTruncated = true;
            if (representsDiff)
            {
                metadata["diffTruncated"] = "true";
            }
        }

        if (CalculateSize(output, error, metadata) > maximumCharacters)
        {
            foreach (var key in metadata.Keys
                         .Where(static key => key != "diff")
                         .OrderBy(key => ImportantMetadataKeys.Contains(key) ? 1 : 0)
                         .ThenByDescending(key => metadata[key].Length)
                         .ThenBy(static key => key, StringComparer.Ordinal)
                         .ToArray())
            {
                if (CalculateSize(output, error, metadata) <= maximumCharacters)
                {
                    break;
                }

                if (metadata[key].Length <= MetadataTruncationMessage.Length)
                {
                    continue;
                }

                metadata[key] = MetadataTruncationMessage;
                metadataWasTruncated = true;
            }
        }

        if (CalculateSize(output, error, metadata) > maximumCharacters)
        {
            foreach (var key in metadata.Keys
                         .Where(key => !ImportantMetadataKeys.Contains(key))
                         .OrderBy(static key => key, StringComparer.Ordinal)
                         .ToArray())
            {
                if (CalculateSize(output, error, metadata) <= maximumCharacters)
                {
                    break;
                }

                metadata.Remove(key);
                metadataWasTruncated = true;
            }
        }

        if (CalculateSize(output, error, metadata) > maximumCharacters)
        {
            var metadataSize = CalculateMetadataSize(metadata);
            var errorBudget = Math.Max(0, maximumCharacters - metadataSize - output.Length);
            if (error is not null && error.Length > errorBudget)
            {
                error = TruncateWithSuffix(
                    error,
                    errorBudget,
                    $" {ToolResultTruncationMessage}");
            }
        }

        if (CalculateSize(output, error, metadata) > maximumCharacters)
        {
            foreach (var key in metadata.Keys
                         .Where(key => key is not "truncated" and not "diffTruncated")
                         .OrderBy(static key => key, StringComparer.Ordinal)
                         .ToArray())
            {
                if (CalculateSize(output, error, metadata) <= maximumCharacters)
                {
                    break;
                }

                metadata.Remove(key);
                metadataWasTruncated = true;
            }
        }

        if (CalculateSize(output, error, metadata) > maximumCharacters)
        {
            var available = Math.Max(0, maximumCharacters - CalculateMetadataSize(metadata));
            if (error is not null)
            {
                error = TruncateWithoutSplittingSurrogate(error, available);
                available = Math.Max(0, available - error.Length);
            }

            output = TruncateWithoutSplittingSurrogate(output, available);
            outputWasTruncated = outputWasTruncated || output.Length != result.Output.Length;
        }

        if (CalculateSize(output, error, metadata) > maximumCharacters)
        {
            metadata.Clear();
            metadataWasTruncated = true;
            var available = maximumCharacters;
            if (error is not null)
            {
                error = TruncateWithoutSplittingSurrogate(error, available);
                available -= error.Length;
            }

            output = TruncateWithoutSplittingSurrogate(output, Math.Max(0, available));
            outputWasTruncated = outputWasTruncated || output.Length != result.Output.Length;
        }

        var limited = result.Succeeded
            ? AgentToolResult.Success(output, metadata)
            : AgentToolResult.Failure(
                string.IsNullOrWhiteSpace(error) ? "Tool failed." : error,
                output,
                metadata,
                result.FailureClassification);
        return new AgentToolResultLimitOutcome(
            limited,
            true,
            outputWasTruncated,
            metadataWasTruncated);
    }

    public static int CalculateSize(AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return CalculateSize(result.Output, result.Error, result.Metadata);
    }

    private static int CalculateSize(
        string output,
        string? error,
        IReadOnlyDictionary<string, string> metadata) =>
        output.Length +
        (error?.Length ?? 0) +
        CalculateMetadataSize(metadata);

    private static int CalculateMetadataSize(IReadOnlyDictionary<string, string> metadata) =>
        metadata.Sum(static pair => pair.Key.Length + pair.Value.Length);

    private static string TruncateWithSuffix(
        string value,
        int maximumCharacters,
        string suffix)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        if (maximumCharacters <= suffix.Length)
        {
            return TruncateWithoutSplittingSurrogate(suffix, maximumCharacters);
        }

        var prefixLength = maximumCharacters - suffix.Length;
        return TruncateWithoutSplittingSurrogate(value, prefixLength) + suffix;
    }

    private static string TruncateWithoutSplittingSurrogate(
        string value,
        int maximumCharacters)
    {
        if (maximumCharacters <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        var length = maximumCharacters;
        if (length > 0 &&
            char.IsHighSurrogate(value[length - 1]) &&
            length < value.Length &&
            char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }
}
