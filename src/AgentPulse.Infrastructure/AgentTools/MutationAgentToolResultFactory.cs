using System.Text;
using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Infrastructure.Mutations;

namespace AgentPulse.Infrastructure.AgentTools;

internal static class MutationAgentToolResultFactory
{
    public static AgentToolResult Success(string operation, FileMutationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Changed paths:");
        foreach (var path in result.Paths)
        {
            builder.Append("- ").AppendLine(path);
        }

        builder.Append("Operation: ").AppendLine(operation);
        builder.Append("Additions: ").AppendLine(
            result.Additions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append("Deletions: ").AppendLine(
            result.Deletions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(result.Diff))
        {
            builder.AppendLine().Append(result.Diff.TrimEnd());
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["paths"] = JsonSerializer.Serialize(result.Paths),
            ["operation"] = operation,
            ["created"] = JsonSerializer.Serialize(result.Created),
            ["modified"] = JsonSerializer.Serialize(result.Modified),
            ["deleted"] = JsonSerializer.Serialize(result.Deleted),
            ["moved"] = JsonSerializer.Serialize(result.Moved),
            ["additions"] = result.Additions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["deletions"] = result.Deletions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["diffCharacterCount"] = result.Diff.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["diffTruncated"] = "false",
            ["sha256BeforeByPath"] = JsonSerializer.Serialize(result.Sha256Before),
            ["sha256AfterByPath"] = JsonSerializer.Serialize(result.Sha256After),
        };
        if (result.Paths.Count == 1)
        {
            metadata["path"] = result.Paths[0];
        }

        if (result.Sha256Before.Count == 1)
        {
            metadata["sha256Before"] = result.Sha256Before.Values.Single();
        }

        if (result.Sha256After.Count == 1)
        {
            metadata["sha256After"] = result.Sha256After.Values.Single();
        }

        return AgentToolResult.Success(builder.ToString().TrimEnd(), metadata);
    }

    public static AgentToolResult DeterministicFailure(string error) =>
        AgentToolResult.Failure(
            error,
            classification: AgentToolFailureClassification.Deterministic);

    public static AgentToolResult TransientFailure(string error) =>
        AgentToolResult.Failure(
            error,
            classification: AgentToolFailureClassification.Transient);
}
