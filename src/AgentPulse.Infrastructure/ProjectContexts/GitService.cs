using System.Security;
using AgentPulse.Application.Processes;
using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Infrastructure.ProjectContexts;

public sealed class GitService : IGitService
{
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processRunner;
    private readonly TimeSpan _timeout;

    public GitService(IProcessRunner processRunner)
        : this(processRunner, DefaultTimeout)
    {
    }

    public GitService(IProcessRunner processRunner, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(processRunner);

        if (timeout <= TimeSpan.Zero &&
            timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _processRunner = processRunner;
        _timeout = timeout;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitAsync(
                Environment.CurrentDirectory,
                ["--version"],
                cancellationToken);

            return result.ExitCode == 0 &&
                result.StandardOutput.TrimStart().StartsWith(
                    "git version ",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (ProcessExecutableNotFoundException)
        {
            return false;
        }
    }

    public async Task<GitRepositoryInfo?> DiscoverAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        try
        {
            var insideWorktree = await RunGitAsync(
                workingDirectory,
                ["rev-parse", "--is-inside-work-tree"],
                cancellationToken);

            if (insideWorktree.ExitCode != 0 ||
                !string.Equals(
                    insideWorktree.StandardOutput.Trim(),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var topLevel = await ReadPathAsync(
                workingDirectory,
                "--show-toplevel",
                cancellationToken);
            var gitDirectory = await ReadPathAsync(
                workingDirectory,
                "--git-dir",
                cancellationToken);
            var commonDirectory = await ReadPathAsync(
                workingDirectory,
                "--git-common-dir",
                cancellationToken);

            if (topLevel is null || gitDirectory is null || commonDirectory is null ||
                !TryResolveGitPath(workingDirectory, topLevel, out var workingTreeRoot) ||
                !TryResolveGitPath(workingDirectory, gitDirectory, out var resolvedGitDirectory) ||
                !TryResolveGitPath(workingDirectory, commonDirectory, out var resolvedCommonDirectory))
            {
                return null;
            }

            return new GitRepositoryInfo(
                workingTreeRoot,
                resolvedGitDirectory,
                resolvedCommonDirectory,
                !PathsEqual(resolvedGitDirectory, resolvedCommonDirectory));
        }
        catch (ProcessExecutableNotFoundException)
        {
            return null;
        }
    }

    private async Task<string?> ReadPathAsync(
        string workingDirectory,
        string option,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            workingDirectory,
            ["rev-parse", option],
            cancellationToken);

        if (result.ExitCode != 0)
        {
            return null;
        }

        var value = result.StandardOutput.TrimEnd('\r', '\n');
        return string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r')
            ? null
            : value;
    }

    private Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return _processRunner.RunAsync(
            new ProcessRequest("git", arguments, workingDirectory, _timeout),
            cancellationToken);
    }

    private static bool TryResolveGitPath(
        string workingDirectory,
        string path,
        out string resolvedPath)
    {
        try
        {
            var absolutePath = Path.IsPathFullyQualified(path)
                ? path
                : Path.Combine(workingDirectory, path);
            resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
