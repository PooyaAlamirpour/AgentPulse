using AgentPulse.Cli.Commands;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class PromptInputReaderTests
{
    [Fact]
    public async Task Arguments_are_joined_using_node_compatible_quoting()
    {
        var console = new TestConsole();
        var reader = new PromptInputReader(console);

        var prompt = await reader.ReadAsync(
            ["hello world", "say \"hi\"", "--", "tail"],
            CancellationToken.None);

        Assert.Equal("\"hello world\" \"say \\\"hi\\\"\" tail", prompt);
    }

    [Fact]
    public async Task Redirected_stdin_is_appended_after_a_line_feed()
    {
        var console = new TestConsole("from stdin", isInputRedirected: true);
        var reader = new PromptInputReader(console);

        var prompt = await reader.ReadAsync(
            ["from-argument"],
            CancellationToken.None);

        Assert.Equal("from-argument\nfrom stdin", prompt);
    }

    [Fact]
    public async Task Redirected_stdin_read_honors_controlled_cancellation()
    {
        using var input = new BlockingTextReader();
        var console = new TestConsole(input, isInputRedirected: true);
        var reader = new PromptInputReader(console);
        using var cancellationSource = new CancellationTokenSource();

        var readTask = reader.ReadAsync([], cancellationSource.Token);
        await input.WaitUntilReadStartsAsync();
        cancellationSource.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        }
        finally
        {
            input.Release();
            await input.WaitUntilReadCompletesAsync();
        }
    }

    private sealed class BlockingTextReader : TextReader
    {
        private readonly TaskCompletionSource readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release = new(initialState: false);
        private readonly TaskCompletionSource readCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilReadStartsAsync()
        {
            return readStarted.Task;
        }

        public Task WaitUntilReadCompletesAsync()
        {
            return readCompleted.Task;
        }

        public void Release()
        {
            release.Set();
        }

        public override string ReadToEnd()
        {
            readStarted.TrySetResult();

            try
            {
                release.Wait();
                return string.Empty;
            }
            finally
            {
                readCompleted.TrySetResult();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                release.Set();
                release.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
