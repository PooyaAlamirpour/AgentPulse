using AgentPulse.Application.Processes;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Infrastructure.Processes;
using AgentPulse.Infrastructure.ProjectContexts;

namespace AgentPulse.Infrastructure.Tests.ProjectContexts;

public sealed class GitProjectContextIntegrationTests
{
    [GitFact]
    public async Task Repository_and_subdirectory_resolve_to_same_root_and_project_id()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var subdirectory = Directory.CreateDirectory(
            Path.Combine(repository.RepositoryPath, "src", "service")).FullName;
        var factory = CreateFactory();

        var rootContext = await factory.CreateAsync(repository.RepositoryPath);
        var subdirectoryContext = await factory.CreateAsync(subdirectory);

        Assert.True(rootContext.IsGitRepository);
        Assert.Equal(repository.RepositoryPath, rootContext.ProjectRoot);
        Assert.Equal(repository.RepositoryPath, rootContext.GitWorktree);
        Assert.Equal(repository.RepositoryPath, subdirectoryContext.ProjectRoot);
        Assert.Equal(rootContext.ProjectId, subdirectoryContext.ProjectId);
    }

    [GitFact]
    public async Task Real_linked_worktree_uses_its_own_root_and_distinct_project_id()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var worktreePath = Path.Combine(repository.RootPath, "feature-worktree");
        await repository.RunGitAsync(
            ["worktree", "add", "-b", "feature-worktree", worktreePath]);
        var factory = CreateFactory();
        var gitService = new GitService(new SystemProcessRunner());

        var mainContext = await factory.CreateAsync(repository.RepositoryPath);
        var worktreeContext = await factory.CreateAsync(worktreePath);
        var discovered = await gitService.DiscoverAsync(worktreePath);

        var discoveredInfo = Assert.IsType<GitRepositoryInfo>(discovered);
        Assert.True(discoveredInfo.IsLinkedWorktree);
        Assert.Equal(Path.TrimEndingDirectorySeparator(worktreePath), worktreeContext.ProjectRoot);
        Assert.Equal(worktreeContext.ProjectRoot, worktreeContext.GitWorktree);
        Assert.NotEqual(mainContext.ProjectId, worktreeContext.ProjectId);

        var repeated = await factory.CreateAsync(worktreePath);
        Assert.Equal(worktreeContext.ProjectId, repeated.ProjectId);
    }

    [GitFact]
    public async Task Temporary_linked_worktree_cleanup_removes_repository_and_worktree()
    {
        var repository = await TemporaryGitRepository.CreateAsync();
        var rootPath = repository.RootPath;
        var worktreePath = Path.Combine(rootPath, "cleanup-worktree");

        try
        {
            await repository.RunGitAsync(
                ["worktree", "add", "-b", "cleanup-worktree", worktreePath]);
            Assert.True(Directory.Exists(worktreePath));
        }
        finally
        {
            await repository.DisposeAsync();
        }

        Assert.False(Directory.Exists(worktreePath));
        Assert.False(Directory.Exists(rootPath));
    }

    [GitFact]
    public async Task Non_git_directory_is_returned_as_non_git_when_rev_parse_exits_nonzero()
    {
        var root = CreateTemporaryDirectory();

        try
        {
            var factory = CreateFactory();
            var context = await factory.CreateAsync(root);

            Assert.False(context.IsGitRepository);
            Assert.Equal(root, context.ProjectRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProjectContextFactory CreateFactory()
    {
        var fileSystem = new SystemProjectFileSystem();
        var processRunner = new SystemProcessRunner();

        return new ProjectContextFactory(
            fileSystem,
            new GitService(processRunner),
            new SystemClock(),
            new SystemPlatformProvider(),
            new DeterministicProjectIdFactory());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "AgentPulse.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private sealed class TemporaryGitRepository : IAsyncDisposable
    {
        private const int DeleteAttemptCount = 5;
        private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(75);
        private readonly SystemProcessRunner _processRunner = new();
        private readonly List<string> _linkedWorktreePaths = [];

        private TemporaryGitRepository(string rootPath, string repositoryPath)
        {
            RootPath = rootPath;
            RepositoryPath = repositoryPath;
        }

        public string RootPath { get; }

        public string RepositoryPath { get; }

        public static async Task<TemporaryGitRepository> CreateAsync()
        {
            var rootPath = CreateTemporaryDirectory();
            var repositoryPath = Directory.CreateDirectory(
                Path.Combine(rootPath, "repository")).FullName;
            var repository = new TemporaryGitRepository(rootPath, repositoryPath);

            await repository.RunGitAsync(["init"]);
            await repository.RunGitAsync(["config", "user.name", "AgentPulse Tests"]);
            await repository.RunGitAsync(["config", "user.email", "agentpulse@example.invalid"]);
            await repository.RunGitAsync(["config", "commit.gpgSign", "false"]);
            var hooksPath = Directory.CreateDirectory(
                Path.Combine(repositoryPath, ".git", "test-hooks")).FullName;
            await repository.RunGitAsync(["config", "core.hooksPath", hooksPath]);
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "README.md"), "test repository");
            await repository.RunGitAsync(["add", "README.md"]);
            await repository.RunGitAsync(["commit", "-m", "Initial test commit"]);

            return repository;
        }

        public async Task RunGitAsync(IReadOnlyList<string> arguments)
        {
            var result = await _processRunner.RunAsync(
                new ProcessRequest(
                    "git",
                    arguments,
                    RepositoryPath,
                    TimeSpan.FromSeconds(15)));

            Assert.True(
                result.ExitCode == 0,
                $"Git command failed: git {string.Join(" ", arguments)}{Environment.NewLine}{result.StandardError}");

            if (arguments.Count >= 3 &&
                string.Equals(arguments[0], "worktree", StringComparison.Ordinal) &&
                string.Equals(arguments[1], "add", StringComparison.Ordinal))
            {
                _linkedWorktreePaths.Add(Path.GetFullPath(arguments[^1], RepositoryPath));
            }
        }

        public async ValueTask DisposeAsync()
        {
            Exception? gitCleanupFailure = null;

            if (Directory.Exists(RepositoryPath))
            {
                foreach (var worktreePath in _linkedWorktreePaths
                             .Distinct(GetPathComparer())
                             .Reverse())
                {
                    try
                    {
                        var result = await _processRunner.RunAsync(
                            new ProcessRequest(
                                "git",
                                ["worktree", "remove", "--force", worktreePath],
                                RepositoryPath,
                                TimeSpan.FromSeconds(15)));
                        if (result.ExitCode != 0)
                        {
                            gitCleanupFailure = new InvalidOperationException(
                                $"Git could not remove temporary worktree '{Path.GetFileName(worktreePath)}'.");
                        }
                    }
                    catch (Exception exception)
                    {
                        gitCleanupFailure = exception;
                    }
                }

                try
                {
                    await _processRunner.RunAsync(
                        new ProcessRequest(
                            "git",
                            ["worktree", "prune"],
                            RepositoryPath,
                            TimeSpan.FromSeconds(15)));
                }
                catch (Exception exception)
                {
                    gitCleanupFailure ??= exception;
                }
            }

            for (var attempt = 1; attempt <= DeleteAttemptCount; attempt++)
            {
                if (!Directory.Exists(RootPath))
                {
                    return;
                }

                NormalizeAttributes(RootPath);

                try
                {
                    Directory.Delete(RootPath, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    if (attempt == DeleteAttemptCount)
                    {
                        throw new IOException(
                            "The temporary Git repository could not be removed after bounded retries.",
                            gitCleanupFailure is null
                                ? exception
                                : new AggregateException(gitCleanupFailure, exception));
                    }

                    await Task.Delay(DeleteRetryDelay);
                }
            }
        }

        private static void NormalizeAttributes(string rootPath)
        {
            IEnumerable<string> paths;

            try
            {
                paths = Directory
                    .EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories)
                    .Prepend(rootPath)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return;
            }

            foreach (var path in paths)
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or FileNotFoundException or
                        DirectoryNotFoundException)
                {
                    // A concurrent delete attempt may already have removed this entry.
                }
            }
        }

        private static StringComparer GetPathComparer()
        {
            return OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }
    }
}
