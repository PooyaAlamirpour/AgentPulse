using System.Diagnostics;
using System.Globalization;
using System.Net;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.TestSupport;
using Microsoft.Data.Sqlite;

namespace AgentPulse.Cli.IntegrationTests;

public sealed partial class CliProcessTests
{
    private const string SecretMarker = "phase9-secret-marker";

    public static TheoryData<string[]> ParserFailureCases => new()
    {
        new[] { "unknown-command" },
        new[] { "--version" },
        new[] { "run", "--unknown" },
        new[] { "run", "--dir" },
        new[] { "run", "--model" },
        new[] { "run", "--session" },
        new[] { "run", "--session", "not-a-session-id", "hello" },
    };

    [Theory]
    [MemberData(nameof(ParserFailureCases))]
    public async Task Parser_and_usage_failures_return_the_usage_exit_code(string[] arguments)
    {
        var result = await RunCliAsync(arguments, standardInput: string.Empty);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.NotEmpty(result.StandardError);
        Assert.DoesNotContain("Session ID:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Positional_prompt_takes_precedence_over_redirected_stdin()
    {
        await using var server = CliLocalModelServer.StartSuccessful();
        var result = await RunCliAsync(
            ["run", "positional prompt"],
            standardInput: "ignored stdin",
            environment: CreateRunEnvironment(server.BaseUrl));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Hello" + Environment.NewLine, result.StandardOutput);
        using var request = System.Text.Json.JsonDocument.Parse(Assert.Single(server.RequestBodies));
        Assert.Equal("positional prompt", GetLastUserContent(request.RootElement));
    }

    [Theory]
    [InlineData("single line")]
    [InlineData("line one\nline two ✓ فارسی")]
    [InlineData("\uFEFFhello with bom")]
    public async Task Redirected_stdin_preserves_supported_text(string prompt)
    {
        await using var server = CliLocalModelServer.StartSuccessful();
        var result = await RunCliAsync(
            ["run"],
            prompt + Environment.NewLine,
            CreateRunEnvironment(server.BaseUrl));

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        using var request = System.Text.Json.JsonDocument.Parse(Assert.Single(server.RequestBodies));
        Assert.Equal(prompt.TrimStart('\uFEFF'), GetLastUserContent(request.RootElement));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task Whitespace_only_stdin_is_a_usage_error(string standardInput)
    {
        var result = await RunCliAsync(["run"], standardInput);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task Path_containing_spaces_is_resolved_without_quotes_in_persistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "Agent Pulse Phase 9", Guid.NewGuid().ToString("N"));
        var projectPath = Directory.CreateDirectory(Path.Combine(root, "My Project")).FullName;
        var runtimeRoot = Directory.CreateDirectory(Path.Combine(root, "runtime data")).FullName;
        var databasePath = Path.Combine(runtimeRoot, "agentpulse.db");

        try
        {
            await using var server = CliLocalModelServer.StartSuccessful();
            var result = await RunCliAsync(
                ["run", "--dir", projectPath, "hello"],
                string.Empty,
                CreateRunEnvironment(server.BaseUrl, runtimeRoot));

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath)),
                await ReadScalarStringAsync(databasePath, "SELECT NormalizedRootPath FROM Projects LIMIT 1;"));
            Assert.DoesNotContain('"', await ReadScalarStringAsync(
                databasePath,
                "SELECT NormalizedRootPath FROM Projects LIMIT 1;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Nonexistent_directory_fails_before_database_or_provider_use()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentPulse Phase 9 Invalid Directory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "agentpulse.db");
        var invalidPath = Path.Combine(root, "missing directory");

        try
        {
            await using var server = CliLocalModelServer.StartSuccessful();
            var result = await RunCliAsync(
                ["run", "--dir", invalidPath, "hello"],
                string.Empty,
                CreateRunEnvironment(server.BaseUrl, root));

            Assert.Equal(ExitCodes.Usage, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("does not exist", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(server.RequestBodies);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task File_passed_as_directory_fails_before_database_or_provider_use()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentPulse Phase 9 File Directory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Combine(root, "not-a-directory.txt");
        await File.WriteAllTextAsync(filePath, "content");

        try
        {
            await using var server = CliLocalModelServer.StartSuccessful();
            var result = await RunCliAsync(
                ["run", "--dir", filePath, "hello"],
                string.Empty,
                CreateRunEnvironment(server.BaseUrl, root));

            Assert.Equal(ExitCodes.Usage, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("not a directory", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(server.RequestBodies);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Well_formed_missing_session_returns_session_exit_code_without_creating_messages()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Missing Session");
        var databasePath = Path.Combine(root, "agentpulse.db");

        try
        {
            await using var server = CliLocalModelServer.StartSuccessful();
            var result = await RunCliAsync(
                ["run", "--session", Guid.NewGuid().ToString("D"), "hello"],
                string.Empty,
                CreateRunEnvironment(server.BaseUrl, root));

            Assert.Equal(ExitCodes.Session, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("does not exist", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(server.RequestBodies);
            Assert.Equal(0L, await ReadScalarInt64Async(databasePath, "SELECT COUNT(*) FROM Messages;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Session_from_another_project_is_rejected_without_creating_messages()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Session Mismatch");
        var runtimeRoot = Directory.CreateDirectory(Path.Combine(root, "runtime")).FullName;
        var firstProject = Directory.CreateDirectory(Path.Combine(root, "project one")).FullName;
        var secondProject = Directory.CreateDirectory(Path.Combine(root, "project two")).FullName;
        var databasePath = Path.Combine(runtimeRoot, "agentpulse.db");

        try
        {
            await using var firstServer = CliLocalModelServer.StartSuccessful();
            var first = await RunCliAsync(
                ["run", "--dir", firstProject, "first"],
                string.Empty,
                CreateRunEnvironment(firstServer.BaseUrl, runtimeRoot));
            var sessionId = ParseSessionId(first.StandardError);
            var messageCount = await ReadScalarInt64Async(databasePath, "SELECT COUNT(*) FROM Messages;");

            await using var secondServer = CliLocalModelServer.StartSuccessful();
            var second = await RunCliAsync(
                ["run", "--dir", secondProject, "--session", sessionId.ToString("D"), "second"],
                string.Empty,
                CreateRunEnvironment(secondServer.BaseUrl, runtimeRoot));

            Assert.Equal(ExitCodes.Session, second.ExitCode);
            Assert.Equal(string.Empty, second.StandardOutput);
            Assert.Contains("different project", second.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(secondServer.RequestBodies);
            Assert.Equal(messageCount, await ReadScalarInt64Async(databasePath, "SELECT COUNT(*) FROM Messages;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Theory]
    [InlineData("AgentPulse__Model__BaseUrl", "not-an-absolute-url")]
    [InlineData("AgentPulse__Model__Model", "")]
    [InlineData("AgentPulse__Model__ApiKeyEnvironmentVariable", "")]
    public async Task Invalid_explicit_model_configuration_returns_configuration_exit_code(
        string key,
        string value)
    {
        var environment = CreateRunEnvironment("http://127.0.0.1:1/v1");
        environment[key] = value;

        var result = await RunCliAsync(["run", "hello"], string.Empty, environment);

        Assert.Equal(ExitCodes.Configuration, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("configuration", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_explicit_configuration_uses_built_in_defaults_then_reports_missing_credential()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Built In Defaults");

        try
        {
            var isolatedAssemblyPath = CopyCliRuntimeWithoutAppSettings(root);
            var result = await RunCliAsync(
                ["run", "hello"],
                string.Empty,
                CreateEnvironmentWithoutModelOverrides(root),
                isolatedAssemblyPath);

            Assert.Equal(ExitCodes.Configuration, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("Set MIMO_API_KEY", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("agentpulse auth set", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "configuration is missing",
                result.StandardError,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "provider configuration",
                result.StandardError,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected the API credential")]
    [InlineData(HttpStatusCode.Forbidden, "denied access")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "temporarily unavailable")]
    public async Task Provider_http_failures_have_stable_exit_code_and_safe_messages(
        HttpStatusCode statusCode,
        string expectedMessage)
    {
        await using var server = CliLocalModelServer.StartError(statusCode);
        var result = await RunCliAsync(
            ["run", "hello"],
            string.Empty,
            CreateRunEnvironment(server.BaseUrl));

        Assert.Equal(ExitCodes.Provider, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains(expectedMessage, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{\"error\"", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_byte_timeout_is_distinct_from_user_cancellation()
    {
        await using var server = CliLocalModelServer.StartHanging();
        var environment = CreateRunEnvironment(server.BaseUrl);
        environment["AgentPulse__Model__FirstByteTimeout"] = "00:00:00.250";

        var result = await RunCliAsync(["run", "hello"], string.Empty, environment);

        Assert.Equal(ExitCodes.Timeout, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_provider_failure_preserves_stdout_and_persistence_and_allows_continuation()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Partial Failure");
        var databasePath = Path.Combine(root, "agentpulse.db");

        try
        {
            await using var failureServer = CliLocalModelServer.StartPartialThenFailure();
            var failed = await RunCliAsync(
                ["run", "first"],
                string.Empty,
                CreateRunEnvironment(failureServer.BaseUrl, root));

            Assert.Equal(ExitCodes.Provider, failed.ExitCode);
            Assert.Equal(CliLocalModelServer.ExpectedPartialText, failed.StandardOutput);
            Assert.Contains("invalid response", failed.StandardError, StringComparison.OrdinalIgnoreCase);
            var sessionId = await ReadSingleSessionIdAsync(databasePath);
            await AssertTerminalRunStateAsync(
                databasePath,
                "Failed",
                CliLocalModelServer.ExpectedPartialText,
                expectedLeaseCount: 0);

            await using var successServer = CliLocalModelServer.StartSuccessful();
            var continued = await RunCliAsync(
                ["run", "--session", sessionId.ToString("D"), "continue"],
                string.Empty,
                CreateRunEnvironment(successServer.BaseUrl, root));

            Assert.Equal(ExitCodes.Success, continued.ExitCode);
            using var request = System.Text.Json.JsonDocument.Parse(Assert.Single(successServer.RequestBodies));
            var roles = request.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("role").GetString())
                .ToArray();
            Assert.Equal(new string?[] { "system", "user", "user" }, roles);
            Assert.DoesNotContain("assistant", roles);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Session_busy_is_stable_and_does_not_create_an_extra_message()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Busy Session");
        var databasePath = Path.Combine(root, "agentpulse.db");

        try
        {
            await using var hangingServer = CliLocalModelServer.StartHanging();
            var environment = CreateRunEnvironment(hangingServer.BaseUrl, root);
            using var firstProcess = StartCliProcess(["run", "first"], environment);
            firstProcess.StandardInput.Close();
            var firstOutput = firstProcess.StandardOutput.ReadToEndAsync();
            var firstError = firstProcess.StandardError.ReadToEndAsync();
            await hangingServer.RequestReceived.WaitAsync(TimeSpan.FromSeconds(10));
            var sessionId = await WaitForSingleSessionIdAsync(databasePath, CancellationToken.None);
            var initialMessageCount = await ReadScalarInt64Async(databasePath, "SELECT COUNT(*) FROM Messages;");

            await using var secondServer = CliLocalModelServer.StartSuccessful();
            var secondEnvironment = CreateRunEnvironment(secondServer.BaseUrl, root);
            var second = await RunCliAsync(
                ["run", "--session", sessionId.ToString("D"), "second"],
                string.Empty,
                secondEnvironment);

            Assert.Equal(ExitCodes.Session, second.ExitCode);
            Assert.Equal(string.Empty, second.StandardOutput);
            Assert.Contains("active run", second.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(secondServer.RequestBodies);
            Assert.Equal(initialMessageCount, await ReadScalarInt64Async(databasePath, "SELECT COUNT(*) FROM Messages;"));

            firstProcess.Kill(entireProcessTree: true);
            await firstProcess.WaitForExitAsync(CancellationToken.None);
            _ = await firstOutput;
            _ = await firstError;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Crash_after_partial_checkpoint_is_recovered_by_the_next_run()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Crash Recovery");
        var databasePath = Path.Combine(root, "agentpulse.db");

        try
        {
            await using var hangingServer = CliLocalModelServer.StartHangingAfterPartial();
            var environment = CreateRunEnvironment(hangingServer.BaseUrl, root);
            using var crashedProcess = StartCliProcess(["run", "first"], environment);
            crashedProcess.StandardInput.Close();
            var crashedOutput = crashedProcess.StandardOutput.ReadToEndAsync();
            var crashedError = crashedProcess.StandardError.ReadToEndAsync();

            await hangingServer.PartialResponseSent.WaitAsync(TimeSpan.FromSeconds(10));
            var sessionId = await WaitForPartialCheckpointAsync(
                databasePath,
                CliLocalModelServer.ExpectedPartialText,
                CancellationToken.None);

            crashedProcess.Kill(entireProcessTree: true);
            await crashedProcess.WaitForExitAsync(CancellationToken.None);
            Assert.Contains(CliLocalModelServer.ExpectedPartialText, await crashedOutput, StringComparison.Ordinal);
            _ = await crashedError;

            await ExpireRunLeaseAsync(databasePath, sessionId);

            await using var successServer = CliLocalModelServer.StartSuccessful();
            var recovered = await RunCliAsync(
                ["run", "--session", sessionId.ToString("D"), "recover"],
                string.Empty,
                CreateRunEnvironment(successServer.BaseUrl, root));

            Assert.Equal(ExitCodes.Success, recovered.ExitCode);
            Assert.Equal("Hello" + Environment.NewLine, recovered.StandardOutput);
            Assert.Equal(0L, await ReadScalarInt64Async(databasePath, "SELECT COUNT(*) FROM RunLeases;"));
            Assert.Equal("Idle", await ReadScalarStringAsync(
                databasePath,
                "SELECT Status FROM Sessions LIMIT 1;"));
            Assert.Equal("Failed", await ReadScalarStringAsync(
                databasePath,
                "SELECT Status FROM Messages WHERE Role = 'Assistant' ORDER BY Sequence LIMIT 1;"));
            Assert.Equal(CliLocalModelServer.ExpectedPartialText, await ReadScalarStringAsync(
                databasePath,
                "SELECT p.Text FROM MessageParts p JOIN Messages m ON m.Id = p.MessageId " +
                "WHERE m.Role = 'Assistant' ORDER BY m.Sequence LIMIT 1;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Debug_logging_stays_on_stderr_and_redacts_credentials_and_provider_body()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Redaction");
        var databasePath = Path.Combine(root, "agentpulse.db");

        try
        {
            await using var server = CliLocalModelServer.StartError(
                HttpStatusCode.ServiceUnavailable,
                $"{{\"error\":{{\"message\":\"{SecretMarker}\"}}}}");
            var environment = CreateRunEnvironment(server.BaseUrl, root);
            environment["MIMO_API_KEY"] = SecretMarker;
            environment["Logging__LogLevel__Default"] = "Debug";
            environment["Logging__LogLevel__Microsoft"] = "Warning";

            var result = await RunCliAsync(["run", "prompt must not be logged"], string.Empty, environment);

            Assert.Equal(ExitCodes.Provider, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("CLI failure category", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretMarker, result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretMarker, result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("prompt must not be logged", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretMarker, await ReadDatabaseTextAsync(databasePath), StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Logging_none_keeps_stderr_to_required_metadata_only()
    {
        await using var server = CliLocalModelServer.StartSuccessful();
        var environment = CreateRunEnvironment(server.BaseUrl);
        environment["Logging__LogLevel__Default"] = "None";

        var result = await RunCliAsync(["run", "hello"], string.Empty, environment);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Hello" + Environment.NewLine, result.StandardOutput);
        Assert.Equal($"Session ID: {ParseSessionId(result.StandardError)}{Environment.NewLine}", result.StandardError);
    }

    [Fact]
    public async Task Information_logging_is_stderr_only_and_does_not_change_stdout()
    {
        await using var server = CliLocalModelServer.StartSuccessful();
        var environment = CreateRunEnvironment(server.BaseUrl);
        environment["Logging__LogLevel__Default"] = "Information";
        environment["Logging__LogLevel__Microsoft"] = "Warning";

        var result = await RunCliAsync(["run", "hello"], string.Empty, environment);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal("Hello" + Environment.NewLine, result.StandardOutput);
        Assert.Contains("Run completed.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stdout_redirection_to_a_file_contains_only_the_model_response()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Redirect");
        var outputPath = Path.Combine(root, "stdout.txt");

        try
        {
            await using var server = CliLocalModelServer.StartSuccessful();

            using var process = StartCliProcess(
                ["run", "hello"],
                CreateRunEnvironment(server.BaseUrl, root));

            process.StandardInput.Close();

            var errorTask = process.StandardError.ReadToEndAsync();

            using var timeoutSource =
                new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await using (var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true))
            {
                var copyTask =
                    process.StandardOutput.BaseStream.CopyToAsync(output);

                await process.WaitForExitAsync(timeoutSource.Token);
                await copyTask;
                await output.FlushAsync(timeoutSource.Token);
            }

            Assert.Equal(ExitCodes.Success, process.ExitCode);

            var redirectedOutput =
                await File.ReadAllTextAsync(
                    outputPath,
                    timeoutSource.Token);

            Assert.Equal(
                "Hello" + Environment.NewLine,
                redirectedOutput);

            Assert.NotEqual(
                Guid.Empty,
                ParseSessionId(await errorTask));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Unexpected_persistence_startup_failure_returns_general_failure_without_a_stack_trace()
    {
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Persistence Failure");
        var environment = CreateRunEnvironment("http://127.0.0.1:1/v1", root);
        environment["AgentPulse__Persistence__DatabasePath"] = root;

        try
        {
            var result = await RunCliAsync(["run", "hello"], string.Empty, environment);

            Assert.Equal(ExitCodes.Failure, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Contains("unexpectedly", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" at ", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain(root, result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    public async Task Help_documents_the_phase_nine_stream_contract()
    {
        var result = await RunCliAsync(["run", "--help"], standardInput: null);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains("stdout             Streamed model response only.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("stderr             Session ID after success", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Ctrl+C preserves any partial response and exits with code 130.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Non-interactive runs never open a credential prompt.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    private static string CopyCliRuntimeWithoutAppSettings(string root)
    {
        var sourceAssemblyPath = FindCliAssemblyPath();
        var sourceDirectory = Path.GetDirectoryName(sourceAssemblyPath) ??
            throw new InvalidOperationException("Could not resolve the CLI runtime directory.");
        var targetDirectory = Directory.CreateDirectory(
            Path.Combine(root, "cli-without-appsettings")).FullName;

        foreach (var sourcePath in Directory.EnumerateFiles(
            sourceDirectory,
            "*",
            SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(sourcePath);
            if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        var targetAssemblyPath = Path.Combine(targetDirectory, Path.GetFileName(sourceAssemblyPath));
        Assert.True(
            File.Exists(targetAssemblyPath),
            $"The isolated CLI assembly was not copied to {targetAssemblyPath}.");
        return targetAssemblyPath;
    }

    private static Dictionary<string, string?> CreateEnvironmentWithoutModelOverrides(string root)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["MIMO_API_KEY"] = null,
            ["AgentPulse__Persistence__DatabasePath"] = Path.Combine(root, "agentpulse.db"),
            ["AgentPulse__Security__CredentialRootPath"] = Path.Combine(root, "credentials"),
            ["HOME"] = Path.Combine(root, "home"),
            ["USERPROFILE"] = Path.Combine(root, "profile"),
            ["LOCALAPPDATA"] = Path.Combine(root, "local-app-data"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "xdg-data"),
        };

        foreach (var optionName in new[]
        {
            "BaseUrl",
            "ChatCompletionsPath",
            "Model",
            "AuthenticationMode",
            "ApiKeyHeaderName",
            "ApiKeyEnvironmentVariable",
            "MaxCompletionTokens",
            "ThinkingMode",
            "IncludeThinkingConfiguration",
            "FirstByteTimeout",
            "StreamIdleTimeout",
            "ErrorBodyReadTimeout",
        })
        {
            environment[$"AgentPulse__Model__{optionName}"] = null;
        }

        return environment;
    }

    private static string CreateTemporaryRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

    private static async Task<long> ReadScalarInt64Async(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(CreateConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadScalarStringAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(CreateConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<Guid> ReadSingleSessionIdAsync(string databasePath)
    {
        var value = await ReadScalarStringAsync(databasePath, "SELECT Id FROM Sessions LIMIT 1;");
        return Guid.Parse(value);
    }

    private static async Task<Guid> WaitForSingleSessionIdAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        return await WaitForDatabaseValueAsync<Guid>(
            databasePath,
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id FROM Sessions LIMIT 1;";
                var value = await command.ExecuteScalarAsync(cancellationToken);
                return value is null ? null : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
            },
            cancellationToken);
    }

    private static async Task<Guid> WaitForPartialCheckpointAsync(
        string databasePath,
        string expectedText,
        CancellationToken cancellationToken)
    {
        return await WaitForDatabaseValueAsync<Guid>(
            databasePath,
            async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT s.Id, p.Text FROM Sessions s " +
                    "JOIN Messages m ON m.SessionId = s.Id " +
                    "JOIN MessageParts p ON p.MessageId = m.Id " +
                    "WHERE m.Role = 'Assistant' LIMIT 1;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) ||
                    !string.Equals(reader.GetString(1), expectedText, StringComparison.Ordinal))
                {
                    return null;
                }

                return Guid.Parse(reader.GetString(0));
            },
            cancellationToken);
    }

    private static async Task<T> WaitForDatabaseValueAsync<T>(
        string databasePath,
        Func<SqliteConnection, Task<T?>> query,
        CancellationToken cancellationToken)
        where T : struct
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            timeoutSource.Token.ThrowIfCancellationRequested();

            if (File.Exists(databasePath))
            {
                try
                {
                    await using var connection = new SqliteConnection(CreateConnectionString(databasePath));
                    await connection.OpenAsync(timeoutSource.Token);
                    var result = await query(connection);
                    if (result.HasValue)
                    {
                        return result.Value;
                    }
                }
                catch (SqliteException)
                {
                    // The child process may be between two short SQLite transactions.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeoutSource.Token);
        }
    }

    private static async Task AssertTerminalRunStateAsync(
        string databasePath,
        string expectedAssistantStatus,
        string expectedAssistantText,
        long expectedLeaseCount)
    {
        Assert.Equal(expectedAssistantStatus, await ReadScalarStringAsync(
            databasePath,
            "SELECT Status FROM Messages WHERE Role = 'Assistant' ORDER BY Sequence DESC LIMIT 1;"));
        Assert.Equal(expectedAssistantText, await ReadScalarStringAsync(
            databasePath,
            "SELECT p.Text FROM MessageParts p JOIN Messages m ON m.Id = p.MessageId " +
            "WHERE m.Role = 'Assistant' ORDER BY m.Sequence DESC LIMIT 1;"));
        Assert.Equal("Idle", await ReadScalarStringAsync(
            databasePath,
            "SELECT Status FROM Sessions LIMIT 1;"));
        Assert.Equal(expectedLeaseCount, await ReadScalarInt64Async(
            databasePath,
            "SELECT COUNT(*) FROM RunLeases;"));
    }

    private static async Task ExpireRunLeaseAsync(string databasePath, Guid sessionId)
    {
        await using var connection = new SqliteConnection(CreateConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE RunLeases SET ExpiresAtUtc = $expired WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$expired", DateTime.UtcNow.AddMinutes(-1).Ticks);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<string> ReadDatabaseTextAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(CreateConnectionString(databasePath));
        await connection.OpenAsync();
        var values = new List<string>();

        foreach (var table in new[] { "Projects", "Sessions", "Messages", "MessageParts", "RunLeases" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table};";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    if (!reader.IsDBNull(index))
                    {
                        values.Add(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            }
        }

        return string.Join('\n', values);
    }
    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public async Task Native_process_exit_code_is_read_from_safe_handle()
    {
        PseudoTerminalAvailability.EnsureSupported();
        var process = CliPseudoTerminalProcessHarness.Start(
            typeof(AgentPulse.Cli.TestSupport.Program).Assembly.Location,
            [AgentPulse.Cli.TestSupport.Program.NativeExitCodeProbeCommand, "7"],
            new Dictionary<string, string?>());

        var result = await process.WaitForExitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(7, result.ExitCode);
        Assert.True(process.HasExited);
        Assert.Equal(7, process.ExitCode);

        process.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = process.ExitCode);
    }

    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public async Task Interactive_process_prompts_for_a_credential_without_echoing_or_persisting_it()
    {
        const string secretMarker = "phase9-interactive-secret-marker";
        const string credentialVariable = "PHASE9_INTERACTIVE_KEY";
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Interactive Credential");
        var databasePath = Path.Combine(root, "agentpulse.db");

        try
        {
            await using var server =
                CliLocalModelServer.StartSuccessfulWithExpectedCredential(secretMarker);
            var environment = CreateRunEnvironment(server.BaseUrl, root);
            environment["MIMO_API_KEY"] = null;
            environment["AgentPulse__Model__ApiKeyEnvironmentVariable"] = credentialVariable;
            environment[credentialVariable] = null;
            environment["Logging__LogLevel__Default"] = "Debug";
            environment["Logging__LogLevel__Microsoft"] = "Warning";
            environment["AgentPulse__Model__AuthenticationMode"] = "Bearer";

            PseudoTerminalAvailability.EnsureSupported();
            using var process = CliPseudoTerminalProcessHarness.Start(
                FindCliAssemblyPath(),
                ["run", "interactive credential test"],
                environment);
            await process.WaitForTranscriptAsync(
                $"Enter {credentialVariable}:",
                TimeSpan.FromSeconds(15));

            using (var inputTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await process.WriteLineAsync(secretMarker, inputTimeout.Token);
            }

            var result = await process.WaitForExitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(ExitCodes.Success, result.ExitCode);
            Assert.True(
                result.NormalizedTranscript.Contains(
                    "API credential was not found",
                    StringComparison.Ordinal),
                "The interactive credential notice was not displayed.");
            Assert.True(
                result.NormalizedTranscript.Contains(
                    $"Enter {credentialVariable}:",
                    StringComparison.Ordinal),
                "The interactive credential prompt was not displayed.");
            Assert.True(
                result.NormalizedTranscript.Contains("Hello", StringComparison.Ordinal),
                "The model response was not displayed.");
            Assert.True(
                result.NormalizedTranscript.Contains("Session ID:", StringComparison.Ordinal),
                "The successful session diagnostic was not displayed.");
            Assert.False(
                result.Transcript.Contains(secretMarker, StringComparison.Ordinal),
                "The credential was echoed in the raw terminal transcript.");
            Assert.False(
                result.NormalizedTranscript.Contains(secretMarker, StringComparison.Ordinal),
                "The credential was echoed in the normalized terminal transcript.");
            Assert.False(
                result.LauncherDiagnostics.Contains(secretMarker, StringComparison.Ordinal),
                "The credential was included in launcher diagnostics.");
            Assert.Single(server.RequestBodies);
            Assert.True(server.ExpectedAuthorizationReceived);
            Assert.False(
                (await ReadDatabaseTextAsync(databasePath)).Contains(
                    secretMarker,
                    StringComparison.Ordinal),
                "The credential was persisted in SQLite.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public async Task Interrupt_after_partial_token_preserves_partial_state_and_releases_the_lease()
    {
        const string secretMarker = "phase9-interactive-secret-marker";
        var root = CreateTemporaryRoot("AgentPulse Phase 9 Ctrl C Partial");
        var databasePath = Path.Combine(root, "agentpulse.db");

        try
        {
            await using var server = CliLocalModelServer.StartHangingAfterPartial();
            var environment = CreateRunEnvironment(server.BaseUrl, root);
            environment["MIMO_API_KEY"] = secretMarker;
            using var process = CliInterruptProcessHarness.Start(
                FindCliAssemblyPath(),
                ["run", "hello"],
                environment);
            process.CloseInput();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await server.PartialResponseSent.WaitAsync(TimeSpan.FromSeconds(10));
            var sessionId = await WaitForPartialCheckpointAsync(
                databasePath,
                CliLocalModelServer.ExpectedPartialText,
                CancellationToken.None);
            await process.SendInterruptAsync();
            await CliInterruptProcessHarness.WaitForExitAsync(process);

            var stdout = await outputTask;
            var stderr = await errorTask;
            Assert.Equal(ExitCodes.Cancelled, process.ExitCode);
            Assert.Equal(CliLocalModelServer.ExpectedPartialText, stdout);
            Assert.Contains("cancelled", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" at ", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(secretMarker, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(secretMarker, stderr, StringComparison.Ordinal);
            await AssertTerminalRunStateAsync(
                databasePath,
                "Cancelled",
                CliLocalModelServer.ExpectedPartialText,
                expectedLeaseCount: 0);
            Assert.Equal(
                1,
                await ReadScalarInt64Async(
                    databasePath,
                    "SELECT COUNT(*) FROM Messages WHERE Role = 'User';"));
            Assert.DoesNotContain(
                secretMarker,
                await ReadDatabaseTextAsync(databasePath),
                StringComparison.Ordinal);

            await using var successServer = CliLocalModelServer.StartSuccessful();
            var continued = await RunCliAsync(
                ["run", "--session", sessionId.ToString("D"), "continue"],
                string.Empty,
                CreateRunEnvironment(successServer.BaseUrl, root));
            Assert.Equal(ExitCodes.Success, continued.ExitCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDirectoryWithRetryAsync(root);
        }
    }


    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public void Windows_control_handler_handles_ctrl_c_and_break_only()
    {
        Assert.True(WindowsConsoleControlProtection.IgnoreControlEvent(
            WindowsConsoleControlProtection.CtrlCEvent));
        Assert.True(WindowsConsoleControlProtection.IgnoreControlEvent(
            WindowsConsoleControlProtection.CtrlBreakEvent));
        Assert.False(WindowsConsoleControlProtection.IgnoreControlEvent(2));
    }

    [Fact]
    [Trait("Category", "ProcessInterrupt")]
    public void Windows_control_handler_is_unregistered_when_signal_send_throws()
    {
        var registrations = new List<bool>();
        var exception = Assert.Throws<InvalidOperationException>(new Action(() =>
        {
            using var protection = WindowsConsoleControlProtection.Register(
                (_, add) =>
                {
                    registrations.Add(add);
                    return true;
                });
            throw new InvalidOperationException("simulated signal failure");
        }));

        Assert.Equal("simulated signal failure", exception.Message);
        Assert.Equal([true, false], registrations);
    }


}
