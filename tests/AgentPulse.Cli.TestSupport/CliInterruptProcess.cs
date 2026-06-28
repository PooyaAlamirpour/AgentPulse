using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentPulse.Cli.TestSupport;

public static class CliInterruptProcessHarness
{
    private const string InterruptCommand = "interrupt";
    private static readonly Encoding Utf8WithoutBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static InterruptibleCliProcess Start(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliAssemblyPath);

        if (OperatingSystem.IsWindows())
        {
            return WindowsInterruptibleProcessLauncher.Start(
                cliAssemblyPath,
                arguments,
                environment);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return UnixInterruptibleProcessLauncher.Start(
                cliAssemblyPath,
                arguments,
                environment);
        }

        throw new PlatformNotSupportedException(
            "The process interrupt harness supports Windows, Linux, and macOS.");
    }

    internal static string InterruptHelperCommand => InterruptCommand;

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

    private static string FindHelperAssemblyPath()
    {
        var loadedAssemblyPath = typeof(CliInterruptProcessHarness).Assembly.Location;
        var loadedRuntimeConfig = Path.ChangeExtension(loadedAssemblyPath, ".runtimeconfig.json");
        if (File.Exists(loadedRuntimeConfig))
        {
            return loadedAssemblyPath;
        }

        var configuration = new FileInfo(loadedAssemblyPath).Directory?.Parent?.Name ?? "Debug";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentPulse.sln")))
            {
                var helperPath = Path.Combine(
                    directory.FullName,
                    "tests",
                    "AgentPulse.Cli.TestSupport",
                    "bin",
                    configuration,
                    "net8.0",
                    "AgentPulse.Cli.TestSupport.dll");
                if (File.Exists(helperPath) &&
                    File.Exists(Path.ChangeExtension(helperPath, ".runtimeconfig.json")))
                {
                    return helperPath;
                }

                break;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the executable AgentPulse CLI test-support helper.",
            loadedAssemblyPath);
    }

    private static IEnumerable<string> BuildHelperArguments(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments)
    {
        yield return FindHelperAssemblyPath();
        yield return InterruptCommand;
        yield return cliAssemblyPath;
        foreach (var argument in arguments)
        {
            yield return argument;
        }
    }

    internal interface IProcessInterruptSender
    {
        Task SendAsync(
            int processId,
            StreamWriter controlInput,
            CancellationToken cancellationToken);
    }

    public sealed class InterruptibleCliProcess : IDisposable
    {
        private const int CleanupWaitMilliseconds = 5_000;
        private readonly Process _process;
        private readonly StreamWriter _controlInput;
        private readonly IProcessInterruptSender _interruptSender;
        private readonly IDisposable[] _ownedStreams;
        private int _disposed;

        internal InterruptibleCliProcess(
            Process process,
            StreamWriter controlInput,
            StreamReader standardOutput,
            StreamReader standardError,
            IProcessInterruptSender interruptSender,
            params IDisposable[] ownedStreams)
        {
            _process = process;
            _controlInput = controlInput;
            StandardOutput = standardOutput;
            StandardError = standardError;
            _interruptSender = interruptSender;
            _ownedStreams = ownedStreams;
        }

        public int Id
        {
            get
            {
                ThrowIfDisposed();
                return _process.Id;
            }
        }

        public int ExitCode
        {
            get
            {
                ThrowIfDisposed();
                return _process.ExitCode;
            }
        }

        public bool HasExited
        {
            get
            {
                ThrowIfDisposed();
                return HasExitedNoThrow();
            }
        }

        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public void CloseInput()
        {
            // The test helper starts the real CLI with an already-closed stdin stream.
            // Its own stdin remains open as a private control channel on Windows.
        }

        public Task SendInterruptAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _interruptSender.SendAsync(Id, _controlInput, cancellationToken);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _process.WaitForExitAsync(cancellationToken);
        }

        public void Kill()
        {
            ThrowIfDisposed();
            StopProcessTreeIfRunning();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                StopProcessTreeIfRunning();
            }
            finally
            {
                DisposeSafely(_controlInput);
                DisposeSafely(StandardOutput);
                DisposeSafely(StandardError);

                foreach (var stream in _ownedStreams)
                {
                    DisposeSafely(stream);
                }

                DisposeSafely(_process);
            }
        }

        private void StopProcessTreeIfRunning()
        {
            try
            {
                if (HasExitedNoThrow())
                {
                    return;
                }

                _process.Kill(entireProcessTree: true);
                _ = _process.WaitForExit(CleanupWaitMilliseconds);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Cleanup is best-effort and must not replace the test's primary failure.
            }
        }

        private bool HasExitedNoThrow()
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
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
                // Cleanup is best-effort and must not replace the test's primary failure.
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
        }
    }

    private static class UnixInterruptibleProcessLauncher
    {
        public static InterruptibleCliProcess Start(
            string cliAssemblyPath,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?> environment)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardInputEncoding = Utf8WithoutBom,
                StandardOutputEncoding = Utf8WithoutBom,
                StandardErrorEncoding = Utf8WithoutBom,
                CreateNoWindow = true,
            };
            ApplyBaseEnvironment(startInfo);
            ApplyEnvironment(startInfo, environment);
            foreach (var argument in BuildHelperArguments(cliAssemblyPath, arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = startInfo };
            var signalMaskScope = UnixSignalMaskScope.UnblockInterrupt();
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "The Unix interruptible CLI helper could not be started.");
                }
            }
            catch (Exception exception)
            {
                signalMaskScope.Restore(exception);
                process.Dispose();
                throw;
            }

            try
            {
                signalMaskScope.Restore();
            }
            catch
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        _ = process.WaitForExit(5_000);
                    }
                }
                catch (Exception cleanupException) when (
                    cleanupException is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                }

                process.Dispose();
                throw;
            }

            return new InterruptibleCliProcess(
                process,
                process.StandardInput,
                process.StandardOutput,
                process.StandardError,
                new UnixProcessInterruptSender());
        }
    }

    private sealed class UnixProcessInterruptSender : IProcessInterruptSender
    {
        private const int SignalInterrupt = 2;

        public Task SendAsync(
            int processId,
            StreamWriter controlInput,
            CancellationToken cancellationToken)
        {
            _ = controlInput;
            cancellationToken.ThrowIfCancellationRequested();

            if (Kill(-processId, SignalInterrupt) != 0)
            {
                throw new InvalidOperationException(
                    $"Could not send SIGINT to the CLI process group (errno {Marshal.GetLastPInvokeError()}).");
            }

            return Task.CompletedTask;
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int Kill(int processId, int signal);
    }

    private static class WindowsInterruptibleProcessLauncher
    {
        private const uint CreateNewConsole = 0x00000010;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint StartfUseShowWindow = 0x00000001;
        private const uint StartfUseStdHandles = 0x00000100;
        private const ushort SwHide = 0;
        private const uint HandleFlagInherit = 0x00000001;

        public static InterruptibleCliProcess Start(
            string cliAssemblyPath,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?> environment)
        {
            var childInputRead = IntPtr.Zero;
            var parentInputWrite = IntPtr.Zero;
            var parentOutputRead = IntPtr.Zero;
            var childOutputWrite = IntPtr.Zero;
            var parentErrorRead = IntPtr.Zero;
            var childErrorWrite = IntPtr.Zero;
            var environmentBlock = IntPtr.Zero;

            try
            {
                CreatePipePair(out childInputRead, out parentInputWrite, parentReads: false);
                CreatePipePair(out parentOutputRead, out childOutputWrite, parentReads: true);
                CreatePipePair(out parentErrorRead, out childErrorWrite, parentReads: true);

                var startupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfo>(),
                    Flags = StartfUseStdHandles | StartfUseShowWindow,
                    ShowWindow = SwHide,
                    StandardInput = childInputRead,
                    StandardOutput = childOutputWrite,
                    StandardError = childErrorWrite,
                };
                var commandLine = new StringBuilder(BuildWindowsCommandLine(
                    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                    BuildHelperArguments(cliAssemblyPath, arguments)));
                environmentBlock = Marshal.StringToHGlobalUni(
                    BuildWindowsEnvironmentBlock(environment));

                if (!CreateProcess(
                    applicationName: null,
                    commandLine,
                    processAttributes: IntPtr.Zero,
                    threadAttributes: IntPtr.Zero,
                    inheritHandles: true,
                    creationFlags: CreateNewConsole | CreateUnicodeEnvironment,
                    environmentBlock,
                    currentDirectory: null,
                    ref startupInfo,
                    out var processInformation))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The Windows interruptible CLI helper could not be started.");
                }

                CloseHandle(processInformation.ThreadHandle);
                CloseHandle(processInformation.ProcessHandle);
                CloseHandle(childInputRead);
                childInputRead = IntPtr.Zero;
                CloseHandle(childOutputWrite);
                childOutputWrite = IntPtr.Zero;
                CloseHandle(childErrorWrite);
                childErrorWrite = IntPtr.Zero;

                var inputStream = CreateFileStream(parentInputWrite, FileAccess.Write);
                parentInputWrite = IntPtr.Zero;
                var outputStream = CreateFileStream(parentOutputRead, FileAccess.Read);
                parentOutputRead = IntPtr.Zero;
                var errorStream = CreateFileStream(parentErrorRead, FileAccess.Read);
                parentErrorRead = IntPtr.Zero;
                var input = new StreamWriter(inputStream, Utf8WithoutBom) { AutoFlush = true };
                var output = new StreamReader(outputStream, Utf8WithoutBom);
                var error = new StreamReader(errorStream, Utf8WithoutBom);
                var process = Process.GetProcessById(checked((int)processInformation.ProcessId));

                return new InterruptibleCliProcess(
                    process,
                    input,
                    output,
                    error,
                    new WindowsProcessInterruptSender(),
                    inputStream,
                    outputStream,
                    errorStream);
            }
            catch
            {
                CloseIfValid(childInputRead);
                CloseIfValid(parentInputWrite);
                CloseIfValid(parentOutputRead);
                CloseIfValid(childOutputWrite);
                CloseIfValid(parentErrorRead);
                CloseIfValid(childErrorWrite);
                throw;
            }
            finally
            {
                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }
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
                    "Could not create a Windows process pipe.");
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
                    "Could not make the Windows parent pipe handle private.");
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

        private static string BuildWindowsCommandLine(
            string dotnetHostPath,
            IEnumerable<string> arguments)
        {
            return string.Join(
                ' ',
                new[] { dotnetHostPath }
                    .Concat(arguments)
                    .Select(QuoteWindowsArgument));
        }

        private static string QuoteWindowsArgument(string argument)
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

        private static string BuildWindowsEnvironmentBlock(
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
        private struct ProcessInformation
        {
            public IntPtr ProcessHandle;
            public IntPtr ThreadHandle;
            public uint ProcessId;
            public uint ThreadId;
        }

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
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    private sealed class WindowsProcessInterruptSender : IProcessInterruptSender
    {
        public async Task SendAsync(
            int processId,
            StreamWriter controlInput,
            CancellationToken cancellationToken)
        {
            _ = processId;
            await controlInput.WriteLineAsync(InterruptCommand.AsMemory(), cancellationToken);
            await controlInput.FlushAsync(cancellationToken);
        }
    }

    private sealed class UnixSignalMaskScope : IDisposable
    {
        private const int SignalInterrupt = 2;
        // Linux sigset_t is 128 bytes; the same buffer is safely oversized for Darwin.
        private const int SignalSetSize = 128;
        private readonly int _setMaskOperation;
        private IntPtr _previousMask;

        private UnixSignalMaskScope(IntPtr previousMask, int setMaskOperation)
        {
            _previousMask = previousMask;
            _setMaskOperation = setMaskOperation;
        }

        public static UnixSignalMaskScope UnblockInterrupt()
        {
            var platform = PosixSignalMaskOperations.GetCurrentPlatform();
            var signalSet = IntPtr.Zero;
            var previousMask = IntPtr.Zero;
            try
            {
                signalSet = Marshal.AllocHGlobal(SignalSetSize);
                previousMask = Marshal.AllocHGlobal(SignalSetSize);
                if (SigEmptySet(signalSet) != 0)
                {
                    throw new InvalidOperationException(
                        $"Could not initialize the SIGINT mask (errno {Marshal.GetLastPInvokeError()}).");
                }

                if (SigAddSet(signalSet, SignalInterrupt) != 0)
                {
                    throw new InvalidOperationException(
                        $"Could not add SIGINT to the signal mask (errno {Marshal.GetLastPInvokeError()}).");
                }

                var maskResult = PThreadSigMask(
                    PosixSignalMaskOperations.GetUnblockOperation(platform),
                    signalSet,
                    previousMask);

                if (maskResult != 0)
                {
                    throw new InvalidOperationException(
                        "Could not unblock SIGINT for the child process " +
                        $"(pthread_sigmask error {maskResult}).");
                }

                return new UnixSignalMaskScope(
                    previousMask,
                    PosixSignalMaskOperations.GetSetMaskOperation(platform));
            }
            catch
            {
                if (previousMask != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(previousMask);
                }

                throw;
            }
            finally
            {
                if (signalSet != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(signalSet);
                }
            }
        }

        public void Restore(Exception? primaryException = null)
        {
            var previousMask = Interlocked.Exchange(ref _previousMask, IntPtr.Zero);
            if (previousMask == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var restoreResult = PThreadSigMask(
                    _setMaskOperation,
                    previousMask,
                    IntPtr.Zero);
                if (restoreResult == 0)
                {
                    return;
                }

                const string diagnosticKey = "AgentPulse.PosixSignalMaskRestore";
                var diagnostic =
                    $"Could not restore the POSIX signal mask (pthread_sigmask error {restoreResult}).";
                if (primaryException is not null)
                {
                    primaryException.Data[diagnosticKey] = diagnostic;
                    return;
                }

                throw new InvalidOperationException(diagnostic);
            }
            finally
            {
                Marshal.FreeHGlobal(previousMask);
            }
        }

        public void Dispose()
        {
            Restore();
        }

        [DllImport("libc", EntryPoint = "sigemptyset", SetLastError = true)]
        private static extern int SigEmptySet(IntPtr signalSet);

        [DllImport("libc", EntryPoint = "sigaddset", SetLastError = true)]
        private static extern int SigAddSet(IntPtr signalSet, int signal);

        [DllImport("libc", EntryPoint = "pthread_sigmask")]
        private static extern int PThreadSigMask(int how, IntPtr signalSet, IntPtr previousMask);
    }

    public static async Task WaitForExitAsync(InterruptibleCliProcess process)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            process.Kill();
            using var cleanupSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupSource.Token);
            throw new TimeoutException("The CLI process did not exit after the interrupt signal.");
        }
    }
}
