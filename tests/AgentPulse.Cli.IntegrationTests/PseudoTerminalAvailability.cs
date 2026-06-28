namespace AgentPulse.Cli.IntegrationTests;

internal static class PseudoTerminalAvailability
{
    public static void EnsureSupported()
    {
        if (OperatingSystem.IsWindows() &&
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException(
                "ConPTY requires Windows 10 version 1809 or newer.");
        }

        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
            !File.Exists("/usr/bin/script"))
        {
            throw new InvalidOperationException(
                "The platform script command is required for the Unix PTY test.");
        }

        if (!OperatingSystem.IsWindows() &&
            !OperatingSystem.IsLinux() &&
            !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The process PTY harness supports Windows, Linux, and macOS.");
        }
    }
}
