using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AgentPulse.Cli.TestSupport;

public static class CliPseudoTerminalProcessHarness
{
    private static readonly Encoding Utf8WithoutBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static PseudoTerminalCliProcess Start(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliAssemblyPath);
        cliAssemblyPath = Path.GetFullPath(cliAssemblyPath);
        if (!File.Exists(cliAssemblyPath))
        {
            throw new FileNotFoundException(
                "The CLI assembly for the pseudo-terminal process was not found.",
                Path.GetFileName(cliAssemblyPath));
        }

        return OperatingSystem.IsWindows()
            ? WindowsPseudoTerminalLauncher.Start(cliAssemblyPath, arguments, environment)
            : UnixPseudoTerminalLauncher.Start(cliAssemblyPath, arguments, environment);
    }

    private static void ApplyBaseEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    }

    private static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?> environment)
    {
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

    public sealed class PseudoTerminalCliProcess : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
        private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
        private readonly Process _process;
        private readonly SafeProcessHandle? _nativeProcessHandle;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private readonly StreamReader? _launcherError;
        private readonly IDisposable[] _ownedResources;
        private readonly Action? _closeTerminal;
        private readonly StringBuilder _transcript = new();
        private readonly StringBuilder _launcherDiagnostics = new();
        private readonly List<string> _sensitiveInputs = [];
        private readonly object _gate = new();
        private readonly Task _outputPump;
        private readonly Task _errorPump;
        private int _terminalClosed;
        private int _disposed;

        public PseudoTerminalCliProcess(
            Process process,
            StreamWriter input,
            StreamReader output,
            StreamReader? launcherError,
            SafeProcessHandle? nativeProcessHandle = null,
            Action? closeTerminal = null,
            params IDisposable[] ownedResources)
        {
            _process = process;
            _nativeProcessHandle = nativeProcessHandle;
            _input = input;
            _output = output;
            _launcherError = launcherError;
            _ownedResources = ownedResources;
            _closeTerminal = closeTerminal;
            _outputPump = PumpAsync(_output, _transcript);
            _errorPump = launcherError is null
                ? Task.CompletedTask
                : PumpAsync(launcherError, _launcherDiagnostics);
        }

        public int Id
        {
            get
            {
                ThrowIfDisposed();
                return _process.Id;
            }
        }

        public bool HasExited
        {
            get
            {
                ThrowIfDisposed();
                return HasExitedCore();
            }
        }

        public string Transcript
        {
            get
            {
                ThrowIfDisposed();
                return GetTranscript();
            }
        }

        public string NormalizedTranscript
        {
            get
            {
                ThrowIfDisposed();
                return TerminalTranscriptNormalizer.NormalizeForAssertions(GetTranscript());
            }
        }

        public int ExitCode
        {
            get
            {
                ThrowIfDisposed();
                return GetExitCodeCore();
            }
        }

        public async Task WaitForTranscriptAsync(string expectedText, TimeSpan timeout)
        {
            ThrowIfDisposed();
            using var timeoutSource = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var normalizedTranscript =
                        TerminalTranscriptNormalizer.NormalizeForAssertions(GetTranscript());
                    if (normalizedTranscript.Contains(expectedText, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (HasExitedCore())
                    {
                        var exitCode = GetExitCodeCore();
                        DisposeSafely(_input);
                        CloseTerminalOnce();
                        await DrainPumpsWithTimeoutAsync();

                        throw new InvalidOperationException(
                            $"The interactive CLI exited with code {exitCode} " +
                            $"before emitting '{expectedText}'. " +
                            $"Normalized transcript: {SanitizeForDiagnostics(TerminalTranscriptNormalizer.NormalizeForAssertions(GetTranscript()))} " +
                            $"Launcher diagnostics: {SanitizeForDiagnostics(GetLauncherDiagnostics())}");
                    }

                    await Task.Delay(PollInterval, timeoutSource.Token);
                }
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                var exitCode = HasExitedCoreNoThrow()
                    ? GetExitCodeCoreNoThrow()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"
                    : "running";
                throw new TimeoutException(
                    $"The interactive CLI did not emit '{expectedText}' within the test timeout. " +
                    $"Native exit code: {exitCode}. " +
                    $"Normalized transcript: {SanitizeForDiagnostics(TerminalTranscriptNormalizer.NormalizeForAssertions(GetTranscript()))} " +
                    $"Launcher diagnostics: {SanitizeForDiagnostics(GetLauncherDiagnostics())}");
            }
        }

        public async Task WriteLineAsync(string value, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _sensitiveInputs.Add(value);
                }
            }

            await _input.WriteAsync(value.AsMemory(), cancellationToken);
            await _input.WriteAsync("\r".AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }

        public async Task<TerminalProcessResult> WaitForExitAsync(TimeSpan timeout)
        {
            ThrowIfDisposed();
            using var timeoutSource = new CancellationTokenSource(timeout);
            try
            {
                await WaitForProcessExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                var diagnosticExitCode = HasExitedCoreNoThrow()
                    ? GetExitCodeCoreNoThrow()?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"
                    : "running";

                throw new TimeoutException(
                    "The interactive CLI process did not exit within the test timeout. " +
                    $"Native exit code: {diagnosticExitCode}. " +
                    $"Normalized transcript: {SanitizeForDiagnostics(TerminalTranscriptNormalizer.NormalizeForAssertions(GetTranscript()))} " +
                    $"Launcher diagnostics: {SanitizeForDiagnostics(GetLauncherDiagnostics())}");
            }

            var exitCode = GetExitCodeCore();
            DisposeSafely(_input);
            CloseTerminalOnce();
            await DrainPumpsWithTimeoutAsync();
            var transcript = GetTranscript();
            return new TerminalProcessResult(
                exitCode,
                transcript,
                TerminalTranscriptNormalizer.NormalizeForAssertions(transcript),
                SanitizeForDiagnostics(GetLauncherDiagnostics()));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                DisposeSafely(_input);

                if (!HasExitedCoreNoThrow())
                {
                    KillForCleanup();
                    WaitForProcessExitForCleanup();
                }

                CloseTerminalOnce();
                DrainPumpsForCleanup();
            }
            finally
            {
                DisposeSafely(_output);
                if (_launcherError is not null)
                {
                    DisposeSafely(_launcherError);
                }

                foreach (var resource in _ownedResources)
                {
                    DisposeSafely(resource);
                }

                _nativeProcessHandle?.Dispose();
                _process.Dispose();
            }
        }

        private async Task WaitForProcessExitAsync(CancellationToken cancellationToken)
        {
            if (_nativeProcessHandle is null)
            {
                await _process.WaitForExitAsync(cancellationToken);
                return;
            }

            while (!HasExitedCore())
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        private bool HasExitedCore()
        {
            return _nativeProcessHandle is null
                ? _process.HasExited
                : WindowsNativeProcess.HasExited(_nativeProcessHandle);
        }

        private bool HasExitedCoreNoThrow()
        {
            try
            {
                return HasExitedCore();
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        private int GetExitCodeCore()
        {
            return _nativeProcessHandle is null
                ? _process.ExitCode
                : WindowsNativeProcess.GetExitCode(_nativeProcessHandle);
        }

        private int? GetExitCodeCoreNoThrow()
        {
            try
            {
                return GetExitCodeCore();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                return null;
            }
        }

        private async Task TerminateForCleanupAsync()
        {
            if (!HasExitedCoreNoThrow())
            {
                KillForCleanup();
            }

            using var cleanupSource = new CancellationTokenSource(CleanupTimeout);
            try
            {
                await WaitForProcessExitAsync(cleanupSource.Token);
            }
            catch (OperationCanceledException) when (cleanupSource.IsCancellationRequested)
            {
                AppendLauncherDiagnostic("The interactive CLI cleanup timed out.");
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                AppendLauncherDiagnostic(
                    $"The interactive CLI cleanup failed safely ({exception.GetType().Name}).");
            }
        }

        private void KillForCleanup()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                if (_nativeProcessHandle is null)
                {
                    AppendLauncherDiagnostic(
                        $"The pseudo-terminal process tree cleanup failed safely ({exception.GetType().Name}).");
                    return;
                }

                try
                {
                    WindowsNativeProcess.Terminate(_nativeProcessHandle);
                }
                catch (Exception fallbackException) when (
                    fallbackException is InvalidOperationException or Win32Exception)
                {
                    AppendLauncherDiagnostic(
                        $"The native pseudo-terminal cleanup failed safely ({fallbackException.GetType().Name}).");
                }
            }
        }

        private void WaitForProcessExitForCleanup()
        {
            var deadline = Stopwatch.GetTimestamp() +
                (long)(CleanupTimeout.TotalSeconds * Stopwatch.Frequency);
            while (!HasExitedCoreNoThrow() && Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(PollInterval);
            }

            if (!HasExitedCoreNoThrow())
            {
                AppendLauncherDiagnostic("The pseudo-terminal cleanup timed out.");
            }
        }

        private async Task DrainPumpsWithTimeoutAsync()
        {
            try
            {
                await Task.WhenAll(_outputPump, _errorPump).WaitAsync(PumpDrainTimeout);
            }
            catch (TimeoutException)
            {
                AppendLauncherDiagnostic("The pseudo-terminal output drain timed out.");
            }
        }

        private void DrainPumpsForCleanup()
        {
            try
            {
                if (!Task.WhenAll(_outputPump, _errorPump).Wait(PumpDrainTimeout))
                {
                    AppendLauncherDiagnostic("The pseudo-terminal output drain timed out during cleanup.");
                }
            }
            catch (AggregateException exception) when (
                exception.InnerExceptions.All(
                    static inner => inner is IOException or ObjectDisposedException))
            {
            }
        }

        private async Task PumpAsync(TextReader reader, StringBuilder target)
        {
            var buffer = new char[256];
            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory());
                    if (read == 0)
                    {
                        return;
                    }
                    lock (_gate)
                    {
                        target.Append(buffer, 0, read);
                    }
                }
            }
            catch (ObjectDisposedException) when (
                Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _terminalClosed) != 0)
            {
            }
            catch (IOException) when (
                Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _terminalClosed) != 0)
            {
            }
        }

        private void CloseTerminalOnce()
        {
            if (Interlocked.Exchange(ref _terminalClosed, 1) == 0)
            {
                _closeTerminal?.Invoke();
            }
        }

        private void AppendLauncherDiagnostic(string value)
        {
            lock (_gate)
            {
                if (_launcherDiagnostics.Length > 0)
                {
                    _launcherDiagnostics.AppendLine();
                }
                _launcherDiagnostics.Append(value);
            }
        }

        private string GetTranscript()
        {
            lock (_gate)
            {
                return _transcript.ToString();
            }
        }

        private string GetLauncherDiagnostics()
        {
            lock (_gate)
            {
                return _launcherDiagnostics.ToString();
            }
        }

        private static void DisposeSafely(IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException)
            {
            }
        }

        private string SanitizeForDiagnostics(string value)
        {
            var sanitized = value;
            lock (_gate)
            {
                foreach (var sensitiveInput in _sensitiveInputs)
                {
                    sanitized = sanitized.Replace(
                        sensitiveInput,
                        "<redacted-input>",
                        StringComparison.Ordinal);
                }
            }
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                sanitized = sanitized.Replace(
                    userProfile,
                    "<user-profile>",
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
            }

            var tempPath = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                sanitized = sanitized.Replace(
                    tempPath,
                    "<temp>",
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal);
            }

            return sanitized;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
        }
    }

    public sealed record TerminalProcessResult(
        int ExitCode,
        string Transcript,
        string NormalizedTranscript,
        string LauncherDiagnostics);


    private static class WindowsNativeProcess
    {
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitTimeout = 0x00000102;
        private const uint WaitFailed = 0xFFFFFFFF;
        private const uint StillActive = 259;

        public static bool HasExited(SafeProcessHandle processHandle)
        {
            var result = WaitForSingleObject(processHandle, milliseconds: 0);
            return result switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                WaitFailed => throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not inspect the native process state."),
                _ => throw new InvalidOperationException(
                    $"Unexpected native process wait result: 0x{result:X8}."),
            };
        }

        public static int GetExitCode(SafeProcessHandle processHandle)
        {
            if (!GetExitCodeProcess(processHandle, out var exitCode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read the native process exit code.");
            }

            if (exitCode == StillActive)
            {
                throw new InvalidOperationException(
                    "The native process is still active and has no final exit code.");
            }

            return unchecked((int)exitCode);
        }

        public static void Terminate(SafeProcessHandle processHandle)
        {
            if (!TerminateProcess(processHandle, exitCode: 1))
            {
                var error = Marshal.GetLastWin32Error();
                const int accessDenied = 5;
                if (error != accessDenied)
                {
                    throw new Win32Exception(error, "Could not terminate the native process.");
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(
            SafeProcessHandle processHandle,
            out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            SafeProcessHandle handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(
            SafeProcessHandle processHandle,
            uint exitCode);
    }

    private static class UnixPseudoTerminalLauncher
    {
        public static PseudoTerminalCliProcess Start(
            string cliAssemblyPath,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?> environment)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException(
                    "The Unix pseudo-terminal harness supports Linux and macOS.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/script",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8WithoutBom,
                StandardOutputEncoding = Utf8WithoutBom,
                StandardErrorEncoding = Utf8WithoutBom,
                CreateNoWindow = true,
            };
            ApplyBaseEnvironment(startInfo);
            ApplyEnvironment(startInfo, environment);

            if (OperatingSystem.IsMacOS())
            {
                ConfigureMacOsScriptArguments(startInfo, cliAssemblyPath, arguments);
            }
            else
            {
                ConfigureLinuxScriptArguments(startInfo, cliAssemblyPath, arguments);
            }

            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "The Unix pseudo-terminal CLI process could not be started.");
            }

            return new PseudoTerminalCliProcess(
                process,
                process.StandardInput,
                process.StandardOutput,
                process.StandardError);
        }

        private static void ConfigureLinuxScriptArguments(
            ProcessStartInfo startInfo,
            string cliAssemblyPath,
            IReadOnlyList<string> arguments)
        {
            var command = string.Join(
                ' ',
                new[]
                {
                    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                    cliAssemblyPath,
                }
                .Concat(arguments)
                .Select(ShellQuote));

            startInfo.ArgumentList.Add("--quiet");
            startInfo.ArgumentList.Add("--return");
            startInfo.ArgumentList.Add("--flush");
            startInfo.ArgumentList.Add("--command");
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add("/dev/null");
        }

        private static void ConfigureMacOsScriptArguments(
            ProcessStartInfo startInfo,
            string cliAssemblyPath,
            IReadOnlyList<string> arguments)
        {
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("/dev/null");
            startInfo.ArgumentList.Add(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            startInfo.ArgumentList.Add(cliAssemblyPath);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        private static string ShellQuote(string value)
        {
            return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
        }
    }

    private static class WindowsPseudoTerminalLauncher
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const nuint ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint StartfUseStdHandles = 0x00000100;

        public static PseudoTerminalCliProcess Start(
            string cliAssemblyPath,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?> environment)
        {
            var pseudoConsoleInputRead = IntPtr.Zero;
            var parentInputWrite = IntPtr.Zero;
            var parentOutputRead = IntPtr.Zero;
            var pseudoConsoleOutputWrite = IntPtr.Zero;
            var pseudoConsole = IntPtr.Zero;
            var attributeList = IntPtr.Zero;
            var environmentBlock = IntPtr.Zero;
            SafeProcessHandle? nativeProcessHandle = null;
            FileStream? inputStream = null;
            FileStream? outputStream = null;
            StreamWriter? input = null;
            StreamReader? output = null;
            Process? process = null;
            PseudoConsoleResource? pseudoConsoleResource = null;

            try
            {
                CreatePipePair(out pseudoConsoleInputRead, out parentInputWrite, parentReads: false);
                CreatePipePair(out parentOutputRead, out pseudoConsoleOutputWrite, parentReads: true);

                var result = CreatePseudoConsole(
                    new Coord(120, 40),
                    pseudoConsoleInputRead,
                    pseudoConsoleOutputWrite,
                    flags: 0,
                    out pseudoConsole);
                if (result != 0)
                {
                    throw new Win32Exception(result, "Could not create the Windows pseudo console.");
                }

                CloseHandle(pseudoConsoleInputRead);
                pseudoConsoleInputRead = IntPtr.Zero;
                CloseHandle(pseudoConsoleOutputWrite);
                pseudoConsoleOutputWrite = IntPtr.Zero;

                nuint attributeListSize = 0;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not initialize the Windows pseudo-console attributes.");
                }

                if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not attach the pseudo console to the Windows CLI process.");
                }

                var startupInfo = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseStdHandles,
                        StandardInput = IntPtr.Zero,
                        StandardOutput = IntPtr.Zero,
                        StandardError = IntPtr.Zero,
                    },
                    AttributeList = attributeList,
                };
                var dotnetHostPath = ResolveDotnetHostPath();
                var commandLine = new StringBuilder(BuildCommandLine(
                    dotnetHostPath,
                    cliAssemblyPath,
                    arguments));
                environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));
                var workingDirectory = Environment.CurrentDirectory;
                if (!Directory.Exists(workingDirectory))
                {
                    throw new DirectoryNotFoundException(
                        "The Windows pseudo-terminal working directory does not exist.");
                }

                if (!CreateProcess(
                    applicationName: dotnetHostPath,
                    commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: false,
                    creationFlags: ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environmentBlock,
                    currentDirectory: workingDirectory,
                    ref startupInfo,
                    out var processInformation))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The Windows pseudo-terminal CLI process could not be started.");
                }

                var rawProcessHandle = processInformation.ProcessHandle;
                try
                {
                    nativeProcessHandle = new SafeProcessHandle(
                        rawProcessHandle,
                        ownsHandle: true);
                    rawProcessHandle = IntPtr.Zero;
                }
                finally
                {
                    CloseIfValid(processInformation.ThreadHandle);
                    CloseIfValid(rawProcessHandle);
                }

                inputStream = CreateFileStream(parentInputWrite, FileAccess.Write);
                parentInputWrite = IntPtr.Zero;
                input = new StreamWriter(inputStream, Utf8WithoutBom) { AutoFlush = true };
                inputStream = null;

                outputStream = CreateFileStream(parentOutputRead, FileAccess.Read);
                parentOutputRead = IntPtr.Zero;
                output = new StreamReader(outputStream, Utf8WithoutBom);
                outputStream = null;
                process = Process.GetProcessById(checked((int)processInformation.ProcessId));
                pseudoConsoleResource = new PseudoConsoleResource(pseudoConsole);
                pseudoConsole = IntPtr.Zero;

                var terminalProcess = new PseudoTerminalCliProcess(
                    process,
                    input,
                    output,
                    launcherError: null,
                    nativeProcessHandle: nativeProcessHandle,
                    closeTerminal: pseudoConsoleResource.Dispose);

                process = null;
                input = null;
                output = null;
                nativeProcessHandle = null;
                pseudoConsoleResource = null;
                return terminalProcess;
            }
            catch
            {
                TerminateFailedStartSafely(process, nativeProcessHandle);
                DisposeFailedStartResourceSafely(input);
                DisposeFailedStartResourceSafely(output);
                DisposeFailedStartResourceSafely(inputStream);
                DisposeFailedStartResourceSafely(outputStream);
                DisposeFailedStartResourceSafely(process);
                DisposeFailedStartResourceSafely(nativeProcessHandle);
                DisposeFailedStartResourceSafely(pseudoConsoleResource);
                CloseIfValid(pseudoConsoleInputRead);
                CloseIfValid(parentInputWrite);
                CloseIfValid(parentOutputRead);
                CloseIfValid(pseudoConsoleOutputWrite);
                if (pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(pseudoConsole);
                }
                throw;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }
            }
        }

        private static void TerminateFailedStartSafely(
            Process? process,
            SafeProcessHandle? nativeProcessHandle)
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(5_000);
                    return;
                }

                if (nativeProcessHandle is not null &&
                    !nativeProcessHandle.IsInvalid &&
                    !nativeProcessHandle.IsClosed &&
                    !WindowsNativeProcess.HasExited(nativeProcessHandle))
                {
                    WindowsNativeProcess.Terminate(nativeProcessHandle);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    Win32Exception or
                    NotSupportedException)
            {
            }
        }

        private static void DisposeFailedStartResourceSafely(IDisposable? resource)
        {
            if (resource is null)
            {
                return;
            }

            try
            {
                resource.Dispose();
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException)
            {
            }
        }

        private static void CreatePipePair(
            out IntPtr readHandle,
            out IntPtr writeHandle,
            bool parentReads)
        {
            var securityAttributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true,
            };
            if (!CreatePipe(out readHandle, out writeHandle, ref securityAttributes, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create a Windows pseudo-console pipe.");
            }
            var parentHandle = parentReads ? readHandle : writeHandle;
            if (!SetHandleInformation(parentHandle, HandleFlagInherit, 0))
            {
                CloseHandle(readHandle);
                CloseHandle(writeHandle);
                readHandle = IntPtr.Zero;
                writeHandle = IntPtr.Zero;
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not make the Windows pseudo-console parent handle private.");
            }
        }

        private static FileStream CreateFileStream(IntPtr handle, FileAccess access)
        {
            return new FileStream(
                new SafeFileHandle(handle, ownsHandle: true),
                access,
                bufferSize: 4096,
                isAsync: false);
        }

        private static string ResolveDotnetHostPath()
        {
            var configuredPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = configuredPath.Trim().Trim('"');
                var fullConfiguredPath = Path.GetFullPath(configuredPath);
                if (File.Exists(fullConfiguredPath))
                {
                    return fullConfiguredPath;
                }
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), "dotnet.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            throw new FileNotFoundException(
                "Could not locate the dotnet host for the Windows pseudo-terminal process.",
                "dotnet.exe");
        }

        private static string BuildCommandLine(
            string dotnetHostPath,
            string cliAssemblyPath,
            IReadOnlyList<string> arguments)
        {
            return string.Join(
                ' ',
                new[] { dotnetHostPath, cliAssemblyPath }
                    .Concat(arguments)
                    .Select(QuoteArgument));
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 &&
                !argument.Any(static character => char.IsWhiteSpace(character) || character == '"'))
            {
                return argument;
            }

            var builder = new StringBuilder(argument.Length + 2);
            builder.Append('"');
            var backslashCount = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }
                if (character == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                    backslashCount = 0;
                    continue;
                }
                builder.Append('\\', backslashCount);
                backslashCount = 0;
                builder.Append(character);
            }
            builder.Append('\\', backslashCount * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private static string BuildEnvironmentBlock(
            IReadOnlyDictionary<string, string?> overrides)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    values[key] = value;
                }
            }
            values["DOTNET_NOLOGO"] = "1";
            values["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            foreach (var item in overrides)
            {
                if (item.Value is null)
                {
                    values.Remove(item.Key);
                }
                else
                {
                    values[item.Key] = item.Value;
                }
            }
            return string.Concat(
                values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static pair => $"{pair.Key}={pair.Value}\0"));
        }

        private static void CloseIfValid(IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                CloseHandle(handle);
            }
        }

        private sealed class PseudoConsoleResource(IntPtr handle) : IDisposable
        {
            private IntPtr _handle = handle;
            public void Dispose()
            {
                var current = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (current != IntPtr.Zero)
                {
                    ClosePseudoConsole(current);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Coord(short x, short y)
        {
            public readonly short X = x;
            public readonly short Y = y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2Count;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr ProcessHandle;
            public IntPtr ThreadHandle;
            public uint ProcessId;
            public uint ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            Coord size,
            IntPtr input,
            IntPtr output,
            uint flags,
            out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            nuint attribute,
            IntPtr value,
            nuint size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out IntPtr readPipe,
            out IntPtr writePipe,
            ref SecurityAttributes pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetHandleInformation(IntPtr objectHandle, uint mask, uint flags);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateProcessW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environmentBlock,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

internal static partial class TerminalTranscriptNormalizer
{
    [GeneratedRegex("\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex CsiSequenceRegex();

    public static string NormalizeForAssertions(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        return CsiSequenceRegex()
            .Replace(transcript, string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }
}
