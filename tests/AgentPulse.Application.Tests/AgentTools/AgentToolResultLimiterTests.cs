using AgentPulse.Application.AgentTools;

namespace AgentPulse.Application.Tests.AgentTools;

public sealed class AgentToolResultLimiterTests
{
    [Fact]
    public void Large_diff_is_bounded_without_losing_mutation_summary()
    {
        var result = AgentToolResult.Success(
            new string('o', 2_000),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["paths"] = "[\"large.txt\"]",
                ["operation"] = "write",
                ["additions"] = "500",
                ["deletions"] = "0",
                ["diff"] = new string('d', 4_000),
            });

        var limited = AgentToolResultLimiter.Limit(result, 512);

        Assert.True(limited.WasLimited);
        Assert.True(limited.OutputWasTruncated);
        Assert.True(limited.MetadataWasTruncated);
        Assert.True(AgentToolResultLimiter.CalculateSize(limited.Result) <= 512);
        Assert.Equal("true", limited.Result.Metadata["diffTruncated"]);
        Assert.Equal("4000", limited.Result.Metadata["diffCharacterCount"]);
        Assert.Equal("500", limited.Result.Metadata["additions"]);
        Assert.Equal("0", limited.Result.Metadata["deletions"]);
        Assert.NotEqual(new string('d', 4_000), limited.Result.Metadata["diff"]);
    }

    [Fact]
    public void Metadata_cannot_bypass_the_aggregate_output_limit()
    {
        var result = AgentToolResult.Success(
            "ok",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["small"] = "value",
                ["oversized"] = new string('x', 10_000),
            });

        var limited = AgentToolResultLimiter.Limit(result, 256);

        Assert.True(limited.WasLimited);
        Assert.True(limited.MetadataWasTruncated);
        Assert.True(AgentToolResultLimiter.CalculateSize(limited.Result) <= 256);
        Assert.DoesNotContain(new string('x', 128), string.Join("|", limited.Result.Metadata.Values));
    }

    [Fact]
    public void Unicode_truncation_does_not_split_a_surrogate_pair()
    {
        var result = AgentToolResult.Success(string.Concat(Enumerable.Repeat("🙂", 200)));

        var limited = AgentToolResultLimiter.Limit(result, 101);

        Assert.True(limited.WasLimited);
        Assert.True(AgentToolResultLimiter.CalculateSize(limited.Result) <= 101);
        Assert.False(
            limited.Result.Output.Length > 0 &&
            char.IsHighSurrogate(limited.Result.Output[^1]));
    }

    [Fact]
    public void Failure_error_and_classification_survive_output_truncation()
    {
        const string error = "Deferred permission authorization is not configured for tool 'example'. Execution was denied.";
        var result = AgentToolResult.Failure(
            error,
            new string('o', 5_000),
            new Dictionary<string, string> { ["detail"] = new string('m', 5_000) },
            AgentToolFailureClassification.Deterministic);

        var limited = AgentToolResultLimiter.Limit(result, 384);

        Assert.False(limited.Result.Succeeded);
        Assert.Equal(AgentToolFailureClassification.Deterministic, limited.Result.FailureClassification);
        Assert.Contains("Deferred permission", limited.Result.Error, StringComparison.Ordinal);
        Assert.True(AgentToolResultLimiter.CalculateSize(limited.Result) <= 384);
    }
}
