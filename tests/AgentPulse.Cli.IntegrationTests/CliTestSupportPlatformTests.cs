using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AgentPulse.Cli.TestSupport;

namespace AgentPulse.Cli.IntegrationTests;

public sealed partial class CliProcessTests
{
    [Theory]
    [InlineData((int)PosixPlatform.Linux, 1, 2)]
    [InlineData((int)PosixPlatform.MacOS, 2, 3)]
    public void Posix_signal_mask_operations_are_platform_specific(
        int platformValue,
        int expectedUnblock,
        int expectedSetMask)
    {
        var platform = (PosixPlatform)platformValue;
        Assert.Equal(
            expectedUnblock,
            PosixSignalMaskOperations.GetUnblockOperation(platform));
        Assert.Equal(
            expectedSetMask,
            PosixSignalMaskOperations.GetSetMaskOperation(platform));
    }

    [Fact]
    public void Error_redirection_alone_does_not_make_a_terminal_non_interactive()
    {
        var console = new TestConsole(
            isInputRedirected: false,
            isOutputRedirected: false,
            isErrorRedirected: true);

        Assert.True(console.IsInteractive);
    }

    [Fact]
    public void Terminal_transcript_normalization_removes_known_vt_sequences_only()
    {
        const string visibleMarker = "visible-text-marker";
        var transcript =
            $"\u001B[?9001h\u001B[?1004hEnter PHASE9_INTERACTIVE_KEY: {visibleMarker}\r\n";

        var normalized = TerminalTranscriptNormalizer.NormalizeForAssertions(transcript);

        Assert.Equal(
            $"Enter PHASE9_INTERACTIVE_KEY: {visibleMarker}\n",
            normalized);
    }

    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public async Task Interactive_process_reports_non_redirected_input_and_output()
    {
        PseudoTerminalAvailability.EnsureSupported();
        using var process = CliPseudoTerminalProcessHarness.Start(
            typeof(AgentPulse.Cli.TestSupport.Program).Assembly.Location,
            [AgentPulse.Cli.TestSupport.Program.RedirectStateProbeCommand],
            new Dictionary<string, string?>());

        var result = await process.WaitForExitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "InputRedirected=False",
            result.NormalizedTranscript,
            StringComparison.Ordinal);
        Assert.Contains(
            "OutputRedirected=False",
            result.NormalizedTranscript,
            StringComparison.Ordinal);
        Assert.Contains(
            "ErrorRedirected=",
            result.NormalizedTranscript,
            StringComparison.Ordinal);
        Assert.Contains(
            "UserInteractive=",
            result.NormalizedTranscript,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public async Task Pseudo_terminal_cleanup_terminates_a_running_process()
    {
        PseudoTerminalAvailability.EnsureSupported();
        var process = CliPseudoTerminalProcessHarness.Start(
            typeof(AgentPulse.Cli.TestSupport.Program).Assembly.Location,
            [AgentPulse.Cli.TestSupport.Program.ProcessTreeProbeCommand],
            new Dictionary<string, string?>());

        try
        {
            await process.WaitForTranscriptAsync(
                "PROCESS_TREE_READY",
                TimeSpan.FromSeconds(15));
            var processIds = ParseProcessTreeIds(process.NormalizedTranscript);
            Assert.False(process.HasExited);

            process.Dispose();
            process.Dispose();

            await AssertProcessExitedAsync(processIds.Parent);
            await AssertProcessExitedAsync(processIds.Child);
            Assert.Throws<ObjectDisposedException>(() => _ = process.HasExited);
        }
        finally
        {
            process.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public async Task Interrupt_process_cleanup_terminates_a_running_process_tree()
    {
        var process = CliInterruptProcessHarness.Start(
            typeof(AgentPulse.Cli.TestSupport.Program).Assembly.Location,
            [AgentPulse.Cli.TestSupport.Program.ProcessTreeProbeCommand],
            new Dictionary<string, string?>());

        try
        {
            var helperProcessId = process.Id;
            var readyLine = await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            var processIds = ParseProcessTreeIds(readyLine ?? string.Empty);
            Assert.False(process.HasExited);

            process.Dispose();
            process.Dispose();

            await AssertProcessExitedAsync(helperProcessId);
            await AssertProcessExitedAsync(processIds.Parent);
            await AssertProcessExitedAsync(processIds.Child);
            Assert.Throws<ObjectDisposedException>(() => _ = process.HasExited);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static (int Parent, int Child) ParseProcessTreeIds(string transcript)
    {
        var match = Regex.Match(
            transcript,
            @"PROCESS_TREE_READY Parent=(?<parent>\d+) Child=(?<child>\d+)",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The process-tree probe did not report its process IDs.");
        return (
            int.Parse(match.Groups["parent"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["child"].Value, CultureInfo.InvariantCulture));
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(TimeSpan.FromSeconds(5).TotalSeconds * Stopwatch.Frequency);
        while (IsProcessRunning(processId) && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.False(IsProcessRunning(processId), "A process remained alive after cleanup.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
