using AgentPulse.Application.Processes;
using AgentPulse.Infrastructure.Processes;

namespace AgentPulse.Infrastructure.Tests.Processes;

public sealed class SystemProcessRunnerTests
{
    [Fact]
    public async Task Standard_output_standard_error_and_exit_code_are_captured_separately()
    {
        var runner = new SystemProcessRunner();
        var request = CreateShellRequest(
            OperatingSystem.IsWindows()
                ? "echo output & echo error 1>&2 & exit /b 7"
                : "printf output; printf error >&2; exit 7",
            TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(request);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("error", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_executable_has_distinct_error()
    {
        var runner = new SystemProcessRunner();
        var request = new ProcessRequest(
            $"agentpulse-missing-{Guid.NewGuid():N}",
            [],
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<ProcessExecutableNotFoundException>(
            () => runner.RunAsync(request));
    }

    [Fact]
    public async Task Invalid_working_directory_is_reported_as_process_start_failure()
    {
        var runner = new SystemProcessRunner();
        var request = new ProcessRequest(
            OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? ["/c", "exit", "0"] : ["-c", "exit 0"],
            Path.Combine(Path.GetTempPath(), $"agentpulse-missing-{Guid.NewGuid():N}"),
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<ProcessStartException>(() => runner.RunAsync(request));
    }

    [Fact]
    public async Task Timeout_stops_process_and_has_distinct_error()
    {
        var runner = new SystemProcessRunner();
        var request = CreateShellRequest(LongRunningCommand(), TimeSpan.FromMilliseconds(150));

        var exception = await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => runner.RunAsync(request));

        Assert.Equal(request.Timeout, exception.Timeout);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_timeout()
    {
        var runner = new SystemProcessRunner();
        var request = CreateShellRequest(LongRunningCommand(), TimeSpan.FromSeconds(30));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(request, cancellationSource.Token));
    }

    private static ProcessRequest CreateShellRequest(string command, TimeSpan timeout)
    {
        return OperatingSystem.IsWindows()
            ? new ProcessRequest(
                "cmd.exe",
                ["/d", "/s", "/c", command],
                Environment.CurrentDirectory,
                timeout)
            : new ProcessRequest(
                "/bin/sh",
                ["-c", command],
                Environment.CurrentDirectory,
                timeout);
    }

    private static string LongRunningCommand()
    {
        return OperatingSystem.IsWindows()
            ? "ping 127.0.0.1 -n 31 > nul"
            : "sleep 30";
    }
}
