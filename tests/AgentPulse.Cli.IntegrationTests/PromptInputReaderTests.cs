using AgentPulse.Cli.Commands;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class PromptInputReaderTests
{
    [Fact]
    public async Task Positional_prompt_wins_and_redirected_stdin_is_not_read()
    {
        var console = new TestConsole(
            new ThrowingTextReader(),
            isInputRedirected: true);
        var reader = new PromptInputReader(console);

        var prompt = await reader.ReadAsync("hello world", CancellationToken.None);

        Assert.Equal("hello world", prompt);
    }

    [Fact]
    public async Task Redirected_stdin_preserves_multiline_unicode_and_trims_only_final_line_endings()
    {
        var console = new TestConsole(
            "  خط اول\r\nsecond  line\n\n",
            isInputRedirected: true);
        var reader = new PromptInputReader(console);

        var prompt = await reader.ReadAsync(null, CancellationToken.None);

        Assert.Equal("  خط اول\r\nsecond  line", prompt);
    }

    [Fact]
    public async Task Missing_positional_prompt_and_non_redirected_stdin_returns_null()
    {
        var reader = new PromptInputReader(new TestConsole());

        var prompt = await reader.ReadAsync(null, CancellationToken.None);

        Assert.Null(prompt);
    }

    [Fact]
    public async Task Redirected_whitespace_is_returned_for_cli_validation()
    {
        var reader = new PromptInputReader(
            new TestConsole("   \r\n", isInputRedirected: true));

        var prompt = await reader.ReadAsync(null, CancellationToken.None);

        Assert.Equal("   ", prompt);
    }

    [Fact]
    public async Task Redirected_stdin_honors_cancellation()
    {
        var reader = new PromptInputReader(
            new TestConsole("hello", isInputRedirected: true));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(null, cancellationSource.Token));
    }

    private sealed class ThrowingTextReader : TextReader
    {
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("stdin must not be read");
        }
    }
}
