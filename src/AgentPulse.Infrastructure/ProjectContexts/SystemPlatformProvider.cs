using System.Runtime.InteropServices;
using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Infrastructure.ProjectContexts;

public sealed class SystemPlatformProvider : IPlatformProvider
{
    public ProjectPlatform Current
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return ProjectPlatform.Windows;
            }

            if (OperatingSystem.IsLinux())
            {
                return ProjectPlatform.Linux;
            }

            if (OperatingSystem.IsMacOS())
            {
                return ProjectPlatform.MacOs;
            }

            if (OperatingSystem.IsFreeBSD())
            {
                return ProjectPlatform.FreeBsd;
            }

            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ProjectPlatform.Windows
                : ProjectPlatform.Unknown;
        }
    }
}
