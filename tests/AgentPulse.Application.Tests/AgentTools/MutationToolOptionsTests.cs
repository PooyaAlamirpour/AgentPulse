using AgentPulse.Application.AgentTools;

namespace AgentPulse.Application.Tests.AgentTools;

public sealed class MutationToolOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var options = new MutationToolOptions();

        options.Validate();

        Assert.Equal(5 * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(1024 * 1024, options.MaxPatchBytes);
        Assert.Equal(12_000, options.MaxDiffPreviewCharacters);
        Assert.Equal(3, options.DiffContextLines);
        Assert.Empty(options.ProtectedPatterns);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("patch")]
    [InlineData("preview")]
    [InlineData("context")]
    public void Non_positive_limits_are_rejected(string limit)
    {
        var options = new MutationToolOptions();
        switch (limit)
        {
            case "file":
                options.MaxFileBytes = 0;
                break;
            case "patch":
                options.MaxPatchBytes = -1;
                break;
            case "preview":
                options.MaxDiffPreviewCharacters = 0;
                break;
            case "context":
                options.DiffContextLines = -1;
                break;
            default:
                throw new InvalidOperationException("Unknown test limit.");
        }

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secret/**")]
    [InlineData("safe/../secret/**")]
    [InlineData("/absolute/**")]
    [InlineData("C:/absolute/**")]
    [InlineData("C:\\absolute\\**")]
    public void Invalid_protected_patterns_are_rejected(string pattern)
    {
        var options = new MutationToolOptions
        {
            ProtectedPatterns = [pattern],
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Duplicate_protected_patterns_are_rejected_after_separator_normalization()
    {
        var options = new MutationToolOptions
        {
            ProtectedPatterns = ["secure/**", "secure\\**"],
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("duplicate pattern", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_relative_custom_patterns_are_valid()
    {
        var options = new MutationToolOptions
        {
            ProtectedPatterns = ["generated/**", "private/*.json"],
        };

        options.Validate();
    }
}
