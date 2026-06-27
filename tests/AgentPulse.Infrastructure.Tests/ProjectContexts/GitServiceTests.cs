using AgentPulse.Application.Processes;
using AgentPulse.Infrastructure.ProjectContexts;

namespace AgentPulse.Infrastructure.Tests.ProjectContexts;

public sealed class GitServiceTests
{
    [Fact]
    public async Task Missing_git_executable_is_reported_as_unavailable()
    {
        var processRunner = new StubProcessRunner
        {
            Exception = new ProcessExecutableNotFoundException(
                "git",
                new InvalidOperationException("missing")),
        };
        var service = new GitService(processRunner);

        var available = await service.IsAvailableAsync();

        Assert.False(available);
    }

    [Fact]
    public async Task Nonzero_rev_parse_exit_code_is_a_valid_non_git_result()
    {
        var processRunner = new StubProcessRunner();
        processRunner.Results.Enqueue(new ProcessResult(128, string.Empty, "not a repository"));
        var service = new GitService(processRunner);

        var repository = await service.DiscoverAsync("/workspace/project");

        Assert.Null(repository);
    }

    [Fact]
    public async Task Invalid_git_output_does_not_create_repository_info()
    {
        var processRunner = new StubProcessRunner();
        processRunner.Results.Enqueue(new ProcessResult(0, "true\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, "\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, ".git\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, ".git\n", string.Empty));
        var service = new GitService(processRunner);

        var repository = await service.DiscoverAsync("/workspace/project");

        Assert.Null(repository);
    }

    [Fact]
    public async Task Invalid_path_output_is_treated_as_invalid_git_metadata()
    {
        var processRunner = new StubProcessRunner();
        processRunner.Results.Enqueue(new ProcessResult(0, "true\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, "invalid\0path\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, ".git\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, ".git\n", string.Empty));
        var service = new GitService(processRunner);

        var repository = await service.DiscoverAsync("/workspace/project");

        Assert.Null(repository);
    }

    [Fact]
    public async Task Linked_worktree_keeps_working_tree_and_common_repository_separate()
    {
        var workspaceRoot = Path.Combine(
            Path.GetPathRoot(Environment.CurrentDirectory)!,
            "workspace");
        var featureRoot = Path.GetFullPath(Path.Combine(workspaceRoot, "feature"));
        var mainGitDirectory = Path.GetFullPath(
            Path.Combine(workspaceRoot, "main", ".git"));
        var linkedGitDirectory = Path.GetFullPath(
            Path.Combine(mainGitDirectory, "worktrees", "feature"));
        var processRunner = new StubProcessRunner();
        processRunner.Results.Enqueue(new ProcessResult(0, "true\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, featureRoot + "\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, linkedGitDirectory + "\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, mainGitDirectory + "\n", string.Empty));
        var service = new GitService(processRunner);

        var repository = await service.DiscoverAsync(Path.Combine(featureRoot, "src"));

        var info = Assert.IsType<AgentPulse.Application.ProjectContexts.GitRepositoryInfo>(repository);
        Assert.Equal(featureRoot, info.WorkingTreeRoot, PathComparer.Instance);
        Assert.Equal(mainGitDirectory, info.CommonGitDirectory, PathComparer.Instance);
        Assert.True(info.IsLinkedWorktree);
    }

    [Fact]
    public async Task Arguments_are_passed_separately_and_timeout_is_configurable()
    {
        var processRunner = new StubProcessRunner();
        processRunner.Results.Enqueue(new ProcessResult(0, "git version 2.47.0\n", string.Empty));
        var timeout = TimeSpan.FromSeconds(3);
        var service = new GitService(processRunner, timeout);

        var available = await service.IsAvailableAsync();

        Assert.True(available);
        var request = Assert.Single(processRunner.Requests);
        Assert.Equal("git", request.Executable);
        Assert.Equal(new[] { "--version" }, request.Arguments);
        Assert.Equal(timeout, request.Timeout);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Queue<ProcessResult> Results { get; } = new();

        public List<ProcessRequest> Requests { get; } = [];

        public Exception? Exception { get; init; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Results.Dequeue());
        }
    }

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public static PathComparer Instance { get; } = new();

        public bool Equals(string? left, string? right)
        {
            return string.Equals(
                left,
                right,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }

        public int GetHashCode(string value)
        {
            return OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase.GetHashCode(value)
                : StringComparer.Ordinal.GetHashCode(value);
        }
    }
}
