namespace AgentPulse.Cli.TestSupport;

internal enum PosixPlatform
{
    Linux,
    MacOS,
}

internal static class PosixSignalMaskOperations
{
    private const int LinuxSignalUnblock = 1;
    private const int LinuxSignalSetMask = 2;
    private const int DarwinSignalUnblock = 2;
    private const int DarwinSignalSetMask = 3;

    public static int GetUnblockOperation(PosixPlatform platform)
    {
        return platform switch
        {
            PosixPlatform.Linux => LinuxSignalUnblock,
            PosixPlatform.MacOS => DarwinSignalUnblock,
            _ => throw new ArgumentOutOfRangeException(
                nameof(platform),
                platform,
                "Unsupported POSIX platform."),
        };
    }

    public static int GetSetMaskOperation(PosixPlatform platform)
    {
        return platform switch
        {
            PosixPlatform.Linux => LinuxSignalSetMask,
            PosixPlatform.MacOS => DarwinSignalSetMask,
            _ => throw new ArgumentOutOfRangeException(
                nameof(platform),
                platform,
                "Unsupported POSIX platform."),
        };
    }

    public static PosixPlatform GetCurrentPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return PosixPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return PosixPlatform.MacOS;
        }

        throw new PlatformNotSupportedException(
            "POSIX signal-mask operations are supported only on Linux and macOS.");
    }
}
