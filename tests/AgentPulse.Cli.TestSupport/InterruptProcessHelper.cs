using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentPulse.Cli.TestSupport;

internal static class InterruptProcessHelper
{
    public static Task<int> RunAsync(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            return RunWindowsAsync(cliAssemblyPath, arguments);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return RunUnixAsync(cliAssemblyPath, arguments);
        }

        throw new PlatformNotSupportedException(
            "The interrupt helper supports Windows, Linux, and macOS.");
    }

    private static async Task<int> RunUnixAsync(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments)
    {
        if (SetProcessGroup(processId: 0, processGroupId: 0) != 0)
        {
            throw new InvalidOperationException(
                $"Could not create the Unix process group (errno {Marshal.GetLastPInvokeError()}).");
        }

        using var interruptRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGINT,
            static context => context.Cancel = true);
        using var process = StartManagedCli(cliAssemblyPath, arguments);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<int> RunWindowsAsync(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments)
    {
        using var process = WindowsGroupedCliProcess.Start(cliAssemblyPath, arguments);
        var processExit = process.WaitForExitAsync();
        var interruptRequest = Console.In.ReadLineAsync();
        var completed = await Task.WhenAny(processExit, interruptRequest);
        if (completed == processExit)
        {
            await processExit;
            return process.ExitCode;
        }

        var command = await interruptRequest;
        if (string.Equals(
            command,
            CliInterruptProcessHarness.InterruptHelperCommand,
            StringComparison.Ordinal))
        {
            using var protection = WindowsConsoleControlProtection.RegisterNative();
            WindowsGroupedCliProcess.SendBreak(process.ProcessGroupId);
            await processExit;
            return process.ExitCode;
        }

        await processExit;
        return process.ExitCode;
    }

    private static Process StartManagedCli(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cliAssemblyPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The real CLI process could not be started.");
        }

        return process;
    }

    [DllImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    private static extern int SetProcessGroup(int processId, int processGroupId);
}

public static class WindowsConsoleControlProtection
{
    public const uint CtrlCEvent = 0;
    public const uint CtrlBreakEvent = 1;

    public delegate bool ControlHandler(uint controlType);

    private static readonly ControlHandler Handler = IgnoreControlEvent;

    public static bool IgnoreControlEvent(uint controlType)
    {
        return controlType is CtrlCEvent or CtrlBreakEvent;
    }

    public static IDisposable Register(
        Func<ControlHandler, bool, bool> registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!registration(Handler, true))
        {
            throw new InvalidOperationException(
                "Could not register the Windows console-control protection handler.");
        }

        return new RegistrationScope(registration);
    }

    internal static IDisposable RegisterNative()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows console-control protection is only available on Windows.");
        }

        return Register(static (handler, add) => SetConsoleCtrlHandler(handler, add));
    }

    private sealed class RegistrationScope(
        Func<ControlHandler, bool, bool> registration) : IDisposable
    {
        private Func<ControlHandler, bool, bool>? _registration = registration;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _registration, null);
            if (current is not null && !current(Handler, false))
            {
                throw new InvalidOperationException(
                    "Could not unregister the Windows console-control protection handler.");
            }

            GC.KeepAlive(Handler);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ControlHandler handlerRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool add);
}

internal sealed class WindowsGroupedCliProcess : IDisposable
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint CtrlBreakEvent = 1;
    private readonly Process _process;

    private WindowsGroupedCliProcess(Process process)
    {
        _process = process;
    }

    public int ExitCode => _process.ExitCode;
    public uint ProcessGroupId => checked((uint)_process.Id);

    public static WindowsGroupedCliProcess Start(
        string cliAssemblyPath,
        IReadOnlyList<string> arguments)
    {
        var childInputRead = IntPtr.Zero;
        var closedInputWrite = IntPtr.Zero;

        try
        {
            CreateClosedInputPipe(out childInputRead, out closedInputWrite);
            CloseHandle(closedInputWrite);
            closedInputWrite = IntPtr.Zero;

            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseStdHandles,
                StandardInput = childInputRead,
                StandardOutput = GetStdHandle(StandardOutputHandle),
                StandardError = GetStdHandle(StandardErrorHandle),
            };
            var commandLine = new StringBuilder(BuildCommandLine(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                cliAssemblyPath,
                arguments));

            if (!CreateProcess(
                applicationName: null,
                commandLine,
                processAttributes: IntPtr.Zero,
                threadAttributes: IntPtr.Zero,
                inheritHandles: true,
                creationFlags: CreateNewProcessGroup | CreateUnicodeEnvironment,
                environmentBlock: IntPtr.Zero,
                currentDirectory: null,
                ref startupInfo,
                out var processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The grouped Windows CLI process could not be started.");
            }

            CloseHandle(processInformation.ThreadHandle);
            CloseHandle(processInformation.ProcessHandle);
            var process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            return new WindowsGroupedCliProcess(process);
        }
        finally
        {
            CloseIfValid(childInputRead);
            CloseIfValid(closedInputWrite);
        }
    }

    public static void SendBreak(uint processGroupId)
    {
        if (!GenerateConsoleCtrlEvent(CtrlBreakEvent, processGroupId))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not send CTRL+BREAK to the Windows CLI process group.");
        }
    }

    public Task WaitForExitAsync()
    {
        return _process.WaitForExitAsync();
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    private static void CreateClosedInputPipe(
        out IntPtr readHandle,
        out IntPtr writeHandle)
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
                "Could not create the closed stdin pipe for the grouped CLI process.");
        }

        if (!SetHandleInformation(writeHandle, HandleFlagInherit, 0))
        {
            CloseHandle(readHandle);
            CloseHandle(writeHandle);
            readHandle = IntPtr.Zero;
            writeHandle = IntPtr.Zero;
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not isolate the grouped CLI stdin pipe.");
        }
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

    private static void CloseIfValid(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            CloseHandle(handle);
        }
    }

    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;

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

    [DllImport("kernel32.dll", SetLastError = true)]
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
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
