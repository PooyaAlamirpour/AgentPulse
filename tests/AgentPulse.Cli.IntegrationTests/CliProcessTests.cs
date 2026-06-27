using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task Help_runs_successfully_without_requesting_a_credential()
    {
        var result = await RunCliAsync(["--help"], standardInput: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("agentpulse run [--dir <path>] [--model <model>] [--session <id>] [prompt]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "Stream a response from the configured model endpoint.",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "Store the API credential for the current model endpoint.",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "Show the credential status for the current model endpoint.",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Xiaomi MiMo " + "response",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Xiaomi MiMo API " + "credential",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "without changing " + "MIMO_API_KEY",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MIMO_API_KEY", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_interactive_run_without_key_fails_with_clear_instructions()
    {
        var result = await RunCliAsync(
            ["run", "hello"],
            standardInput: string.Empty,
            environment: new Dictionary<string, string?>
            {
                ["MIMO_API_KEY"] = null,
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("Set MIMO_API_KEY", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("agentpulse auth set", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_receives_prompt_from_arguments_and_streams_only_model_text_to_stdout()
    {
        await using var server = CliLocalModelServer.StartSuccessful();
        var result = await RunCliAsync(
            ["run", "hello"],
            standardInput: string.Empty,
            environment: CreateRunEnvironment(server.BaseUrl));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Hello" + Environment.NewLine, result.StandardOutput);
        Assert.NotEqual(Guid.Empty, ParseSessionId(result.StandardError));
    }

    [Fact]
    public async Task Run_receives_prompt_from_redirected_stdin_when_environment_key_exists()
    {
        await using var server = CliLocalModelServer.StartSuccessful();
        var result = await RunCliAsync(
            ["run"],
            standardInput: "hello from pipe",
            environment: CreateRunEnvironment(server.BaseUrl));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Hello" + Environment.NewLine, result.StandardOutput);
        Assert.NotEqual(Guid.Empty, ParseSessionId(result.StandardError));
    }

    [Fact]
    public async Task Empty_prompt_writes_clear_error_to_stderr_and_returns_nonzero_exit_code()
    {
        var result = await RunCliAsync(["run"], standardInput: string.Empty);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains(
            "A prompt is required as an argument or redirected standard input.",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Session ID:", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_options_are_wired_end_to_end_with_git_root_history_and_stderr_metadata()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AgentPulse Cli Tests",
            Guid.NewGuid().ToString("N"));
        var repositoryPath = Directory.CreateDirectory(
            Path.Combine(root, "repository with space")).FullName;
        var subdirectory = Directory.CreateDirectory(
            Path.Combine(repositoryPath, "src", "service")).FullName;
        var environmentRoot = Directory.CreateDirectory(Path.Combine(root, "environment")).FullName;
        var databasePath = Path.Combine(environmentRoot, "agentpulse.db");

        try
        {
            await InitializeGitRepositoryAsync(repositoryPath);
            await using var server = CliLocalModelServer.StartSuccessful(expectedRequestCount: 2);
            var environment = CreateRunEnvironment(server.BaseUrl, environmentRoot);

            var first = await RunCliAsync(
                [
                    "run",
                    "--dir",
                    subdirectory + Path.DirectorySeparatorChar,
                    "--model",
                    "custom-model",
                    "first positional",
                ],
                standardInput: "ignored redirected input",
                environment: environment);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal("Hello" + Environment.NewLine, first.StandardOutput);
            var sessionId = ParseSessionId(first.StandardError);

            const string multilinePrompt = "second line one\nsecond line two ✓";
            var second = await RunCliAsync(
                [
                    "run",
                    "--dir",
                    repositoryPath,
                    "--session",
                    sessionId.ToString("D"),
                ],
                standardInput: multilinePrompt + Environment.NewLine,
                environment: environment);

            Assert.Equal(0, second.ExitCode);
            Assert.Equal("Hello" + Environment.NewLine, second.StandardOutput);
            Assert.Equal(sessionId, ParseSessionId(second.StandardError));

            Assert.Equal(2, server.RequestBodies.Count);
            using var firstRequest = JsonDocument.Parse(server.RequestBodies[0]);
            using var secondRequest = JsonDocument.Parse(server.RequestBodies[1]);
            Assert.Equal(
                "custom-model",
                firstRequest.RootElement.GetProperty("model").GetString());
            Assert.Equal(
                "mimo-v2.5-pro",
                secondRequest.RootElement.GetProperty("model").GetString());
            Assert.Equal(
                "first positional",
                GetLastUserContent(firstRequest.RootElement));
            Assert.Equal(multilinePrompt, GetLastUserContent(secondRequest.RootElement));
            Assert.Equal(
                ["system", "user", "assistant", "user"],
                secondRequest.RootElement
                    .GetProperty("messages")
                    .EnumerateArray()
                    .Select(message => message.GetProperty("role").GetString()));

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT NormalizedRootPath, IsGitRepository, GitWorktree, " +
                "(SELECT COUNT(*) FROM Projects), (SELECT COUNT(*) FROM Sessions) " +
                "FROM Projects;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var canonicalRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(repositoryPath));
            Assert.Equal(canonicalRoot, reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(canonicalRoot, reader.GetString(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(1, reader.GetInt32(4));
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Ctrl_c_cancels_a_running_cli_process_cleanly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await using var server = CliLocalModelServer.StartHanging();
        using var process = StartCliProcessWithDefaultInterruptDisposition(
            server.BaseUrl);
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

        Assert.Equal(130, process.ExitCode);
        Assert.Equal(string.Empty, await standardOutputTask);
        Assert.Contains(
            "Operation cancelled.",
            await standardErrorTask,
            StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> CreateRunEnvironment(
        string baseUrl,
        string? root = null)
    {
        root ??= Path.Combine(
            Path.GetTempPath(),
            "AgentPulse.CliTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new Dictionary<string, string?>
        {
            ["MIMO_API_KEY"] = "process-test-secret",
            ["AgentPulse__Model__BaseUrl"] = baseUrl,
            ["AgentPulse__Persistence__DatabasePath"] = Path.Combine(root, "agentpulse.db"),
            ["AgentPulse__Security__CredentialRootPath"] = Path.Combine(root, "security"),
        };
    }

    private static Guid ParseSessionId(string standardError)
    {
        var line = standardError
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith("Session ID: ", StringComparison.Ordinal));
        return Guid.Parse(line["Session ID: ".Length..]);
    }

    private static string GetLastUserContent(JsonElement request)
    {
        return request
            .GetProperty("messages")
            .EnumerateArray()
            .Last(message => string.Equals(
                message.GetProperty("role").GetString(),
                "user",
                StringComparison.Ordinal))
            .GetProperty("content")
            .GetString()!;
    }

    private static async Task InitializeGitRepositoryAsync(string repositoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("init");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "The test Git process could not be started.");
        await process.WaitForExitAsync(CancellationToken.None);
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, $"Git initialization failed: {error}");
    }

    private static async Task<ProcessResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var process = StartCliProcess(arguments, environment);

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

    private static Process StartCliProcess(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
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

        ApplyBaseEnvironment(startInfo);
        ApplyEnvironment(startInfo, environment);
        startInfo.ArgumentList.Add(cliAssemblyPath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "The CLI process could not be started.");
        return process;
    }

    private static Process StartCliProcessWithDefaultInterruptDisposition(string baseUrl)
    {
        var cliAssemblyPath = FindCliAssemblyPath();
        var dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var command = string.Join(
            " ",
            "trap - INT; exec",
            ShellQuote(dotnetHostPath),
            ShellQuote(cliAssemblyPath),
            "run",
            ShellQuote("hello"));

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        ApplyBaseEnvironment(startInfo);
        ApplyEnvironment(startInfo, CreateRunEnvironment(baseUrl));
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "The CLI process could not be started.");
        return process;
    }

    private static void ApplyBaseEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    }

    private static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach (var item in environment)
        {
            if (item.Value is null)
            {
                startInfo.Environment.Remove(item.Key);
            }
            else
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }
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
