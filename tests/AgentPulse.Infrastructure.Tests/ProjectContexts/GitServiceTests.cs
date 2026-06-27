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
        var processRunner = new StubProcessRunner();
        processRunner.Results.Enqueue(new ProcessResult(0, "true\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, "/workspace/feature\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, "/workspace/main/.git/worktrees/feature\n", string.Empty));
        processRunner.Results.Enqueue(new ProcessResult(0, "/workspace/main/.git\n", string.Empty));
        var service = new GitService(processRunner);

        var repository = await service.DiscoverAsync("/workspace/feature/src");

        var info = Assert.IsType<AgentPulse.Application.ProjectContexts.GitRepositoryInfo>(repository);
        Assert.Equal("/workspace/feature", info.WorkingTreeRoot);
        Assert.Equal("/workspace/main/.git", info.CommonGitDirectory);
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
}
