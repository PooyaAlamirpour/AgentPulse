using System.Diagnostics;

namespace AgentPulse.Infrastructure.Tests.ProjectContexts;

[AttributeUsage(AttributeTargets.Method)]
public sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        if (!IsGitAvailable())
        {
            Skip = "Git integration test skipped because the git executable is not available.";
        }
    }

    private static bool IsGitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--version" },
            });

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
