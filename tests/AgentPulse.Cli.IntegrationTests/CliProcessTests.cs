using System.Diagnostics;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task Help_runs_successfully()
    {
        var result = await RunCliAsync(["--help"], standardInput: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("agentpulse run [message...]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Run_receives_prompt_from_arguments_and_keeps_stderr_empty()
    {
        var result = await RunCliAsync(["run", "hello"], standardInput: string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Run_receives_prompt_from_redirected_stdin()
    {
        var result = await RunCliAsync(["run"], standardInput: "hello from pipe");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello from pipe", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Empty_prompt_writes_clear_error_to_stderr_and_returns_nonzero_exit_code()
    {
        var result = await RunCliAsync(["run"], standardInput: string.Empty);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains(
            "You must provide a message or a command",
            result.StandardError,
            StringComparison.Ordinal);
    }


    [Fact]
    public async Task Ctrl_c_cancels_a_running_cli_process_cleanly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var process = StartCliProcessWithDefaultInterruptDisposition();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await WaitForCliProcessImageAsync(process.Id);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await SendInterruptAsync(process.Id);

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        var combinedOutput = string.Concat(
            await standardOutputTask,
            await standardErrorTask);

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Operation cancelled.", combinedOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        string? standardInput)
    {
        using var process = StartCliProcess(arguments);

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
        }

        process.StandardInput.Close();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(CancellationToken.None);

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }


    private static Process StartCliProcess(IReadOnlyList<string> arguments)
    {
        var cliAssemblyPath = FindCliAssemblyPath();
        var dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHostPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.ArgumentList.Add(cliAssemblyPath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "The CLI process could not be started.");
        return process;
    }

    private static Process StartCliProcessWithDefaultInterruptDisposition()
    {
        var cliAssemblyPath = FindCliAssemblyPath();
        var dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var command = string.Join(
            " ",
            "trap - INT; exec",
            ShellQuote(dotnetHostPath),
            ShellQuote(cliAssemblyPath),
            "run");

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "The CLI process could not be started.");
        return process;
    }

    private static async Task WaitForCliProcessImageAsync(int processId)
    {
        var commandLinePath = $"/proc/{processId}/cmdline";

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(commandLinePath))
            {
                var arguments = System.Text.Encoding.UTF8
                    .GetString(await File.ReadAllBytesAsync(commandLinePath))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);

                if (arguments.Length > 1 &&
                    string.Equals(Path.GetFileName(arguments[0]), "dotnet", StringComparison.Ordinal) &&
                    arguments.Skip(1).Any(static argument =>
                        argument.EndsWith("agentpulse.dll", StringComparison.Ordinal)))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new InvalidOperationException("Could not locate the running CLI process image.");
    }

    private static async Task SendInterruptAsync(int processId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/kill",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-INT");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var signalProcess = new Process { StartInfo = startInfo };
        Assert.True(signalProcess.Start(), "The interrupt signal process could not be started.");
        await signalProcess.WaitForExitAsync(CancellationToken.None);

        var error = await signalProcess.StandardError.ReadToEndAsync();
        Assert.True(signalProcess.ExitCode == 0, $"Could not send interrupt signal: {error}");
    }

    private static string ShellQuote(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static string FindCliAssemblyPath()
    {
        var testAssemblyPath = typeof(CliProcessTests).Assembly.Location;
        var configuration = new FileInfo(testAssemblyPath).Directory?.Parent?.Name ?? "Debug";
        var repositoryRoot = FindRepositoryRoot();
        var cliAssemblyPath = Path.Combine(
            repositoryRoot,
            "src",
            "AgentPulse.Cli",
            "bin",
            configuration,
            "net8.0",
            "agentpulse.dll");

        Assert.True(File.Exists(cliAssemblyPath), $"CLI assembly not found: {cliAssemblyPath}");
        return cliAssemblyPath;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentPulse.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
