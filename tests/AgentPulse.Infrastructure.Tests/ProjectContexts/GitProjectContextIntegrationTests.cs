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
        private readonly SystemProcessRunner _processRunner = new();

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
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
