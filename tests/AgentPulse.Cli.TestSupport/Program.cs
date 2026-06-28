using System.Diagnostics;

namespace AgentPulse.Cli.TestSupport;

public static class Program
{
    public const string NativeExitCodeProbeCommand = "native-exit-code-probe";
    public const string RedirectStateProbeCommand = "redirect-state-probe";
    public const string ProcessTreeProbeCommand = "process-tree-probe";
    private const string ProcessTreeChildCommand = "process-tree-child";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 &&
            string.Equals(args[0], NativeExitCodeProbeCommand, StringComparison.Ordinal) &&
            int.TryParse(args[1], out var requestedExitCode))
        {
            return requestedExitCode;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], RedirectStateProbeCommand, StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync(
                $"InputRedirected={Console.IsInputRedirected};" +
                $"OutputRedirected={Console.IsOutputRedirected};" +
                $"ErrorRedirected={Console.IsErrorRedirected};" +
                $"UserInteractive={Environment.UserInteractive}");
            return 0;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], ProcessTreeProbeCommand, StringComparison.Ordinal))
        {
            return await RunProcessTreeProbeAsync();
        }

        if (args.Length == 1 &&
            string.Equals(args[0], ProcessTreeChildCommand, StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        if (args.Length < 2 ||
            !string.Equals(
                args[0],
                CliInterruptProcessHarness.InterruptHelperCommand,
                StringComparison.Ordinal))
        {
            return 2;
        }

        try
        {
            return await InterruptProcessHelper.RunAsync(args[1], args[2..]);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"Interrupt helper failed safely ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> RunProcessTreeProbeAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(ProcessTreeChildCommand);

        using var child = new Process { StartInfo = startInfo };
        if (!child.Start())
        {
            throw new InvalidOperationException("The process-tree child probe could not be started.");
        }

        await Console.Out.WriteLineAsync(
            $"PROCESS_TREE_READY Parent={Environment.ProcessId} Child={child.Id}");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
