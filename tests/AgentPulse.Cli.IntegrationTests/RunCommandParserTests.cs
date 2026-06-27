using AgentPulse.Cli.Commands;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class RunCommandParserTests
{
    private readonly RunCommandParser _parser = new();

    [Fact]
    public void Positional_prompt_is_joined()
    {
        var parsed = _parser.Parse(["hello", "world"]);

        Assert.Equal("hello world", parsed.PositionalPrompt);
        Assert.Null(parsed.ProjectDirectory);
        Assert.Null(parsed.ModelOverride);
        Assert.Null(parsed.SessionId);
    }

    [Fact]
    public void Options_are_order_independent()
    {
        var sessionId = Guid.NewGuid();

        var parsed = _parser.Parse(
        [
            "hello",
            "--session",
            sessionId.ToString(),
            "--dir",
            "project path",
            "--model",
            " custom-model ",
        ]);

        Assert.Equal("hello", parsed.PositionalPrompt);
        Assert.Equal("project path", parsed.ProjectDirectory);
        Assert.Equal("custom-model", parsed.ModelOverride);
        Assert.Equal(sessionId, parsed.SessionId?.Value);
    }

    [Fact]
    public void Double_dash_stops_option_parsing()
    {
        var parsed = _parser.Parse(["--", "--model", "literal"]);

        Assert.Equal("--model literal", parsed.PositionalPrompt);
        Assert.Null(parsed.ModelOverride);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--dir")]
    [InlineData("--model")]
    [InlineData("--session")]
    public void Invalid_or_missing_option_is_rejected(string option)
    {
        Assert.Throws<RunCommandParsingException>(() => _parser.Parse([option]));
    }

    [Theory]
    [InlineData("--dir", "")]
    [InlineData("--model", " ")]
    [InlineData("--session", "not-a-guid")]
    [InlineData("--session", "00000000-0000-0000-0000-000000000000")]
    public void Invalid_option_value_is_rejected(string option, string value)
    {
        Assert.Throws<RunCommandParsingException>(() => _parser.Parse([option, value]));
    }

    [Theory]
    [InlineData("--dir", "one", "--dir", "two")]
    [InlineData("--model", "one", "--model", "two")]
    [InlineData(
        "--session",
        "11111111-1111-1111-1111-111111111111",
        "--session",
        "22222222-2222-2222-2222-222222222222")]
    public void Duplicate_option_is_rejected(params string[] arguments)
    {
        Assert.Throws<RunCommandParsingException>(() => _parser.Parse(arguments));
    }
}
