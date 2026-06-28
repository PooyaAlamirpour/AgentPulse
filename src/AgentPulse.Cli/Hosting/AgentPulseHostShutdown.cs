using AgentPulse.Cli.Console;
using Microsoft.Extensions.Hosting;

namespace AgentPulse.Cli.Hosting;

public static class AgentPulseHostShutdown
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromSeconds(1);

    public static Task StopAsync(IHost host, IConsole console)
    {
        return StopAsync(host, console, Timeout);
    }

    public static async Task StopAsync(
        IHost host,
        IConsole console,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var shutdownCancellation = new CancellationTokenSource(timeout);

        try
        {
            var stopTask = host.StopAsync(shutdownCancellation.Token);
            await stopTask.WaitAsync(shutdownCancellation.Token);
        }
        catch (OperationCanceledException) when (shutdownCancellation.IsCancellationRequested)
        {
            await WriteDiagnosticAsync(
                console,
                $"Host shutdown exceeded the {timeout.TotalSeconds:0.###}-second limit.");
        }
        catch (Exception)
        {
            await WriteDiagnosticAsync(console, "Host shutdown failed safely.");
        }
    }

    private static async Task WriteDiagnosticAsync(IConsole console, string message)
    {
        using var diagnosticCancellation = new CancellationTokenSource(DiagnosticTimeout);

        try
        {
            await console.Error.WriteLineAsync(
                message.AsMemory(),
                diagnosticCancellation.Token);
            await console.Error.FlushAsync(diagnosticCancellation.Token);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Shutdown diagnostics must never replace the command's primary exit code.
        }
    }
}
