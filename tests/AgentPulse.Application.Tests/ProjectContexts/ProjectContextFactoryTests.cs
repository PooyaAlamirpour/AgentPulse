using AgentPulse.Application.Processes;
using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Application.Tests.ProjectContexts;

public sealed class ProjectContextFactoryTests
{
    private static readonly DateTime UtcNow =
        new(2026, 6, 27, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Absolute_directory_is_normalized_and_preserved_as_current_directory()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/current")
            .AddDirectory("/workspace/project");
        var factory = CreateFactory(fileSystem);

        var context = await factory.CreateAsync("/workspace/project/./src/..");

        Assert.Equal("/workspace/project", context.CurrentDirectory);
        Assert.Equal("/workspace/project", context.ProjectRoot);
        Assert.False(context.IsGitRepository);
        Assert.Null(context.GitWorktree);
    }

    [Fact]
    public async Task Relative_directory_is_resolved_from_process_current_directory()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/current")
            .AddDirectory("/workspace/project");
        var factory = CreateFactory(fileSystem);

        var context = await factory.CreateAsync("../project");

        Assert.Equal("/workspace/project", context.CurrentDirectory);
    }

    [Fact]
    public async Task Empty_path_uses_process_current_directory()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/current")
            .AddDirectory("/workspace/current");
        var factory = CreateFactory(fileSystem);

        var context = await factory.CreateAsync("   ");

        Assert.Equal("/workspace/current", context.CurrentDirectory);
    }

    [Fact]
    public async Task Missing_path_is_rejected_with_application_error()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/current");
        var factory = CreateFactory(fileSystem);

        var exception = await Assert.ThrowsAsync<ProjectContextException>(
            () => factory.CreateAsync("/workspace/missing"));

        Assert.Equal(ProjectContextErrorCode.PathNotFound, exception.ErrorCode);
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_path_is_rejected_with_application_error()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/current")
            .AddFile("/workspace/file.txt");
        var factory = CreateFactory(fileSystem);

        var exception = await Assert.ThrowsAsync<ProjectContextException>(
            () => factory.CreateAsync("/workspace/file.txt"));

        Assert.Equal(ProjectContextErrorCode.PathIsNotDirectory, exception.ErrorCode);
    }

    [Fact]
    public async Task Inaccessible_path_is_rejected_with_application_error()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/current")
            .Deny("/workspace/private");
        var factory = CreateFactory(fileSystem);

        var exception = await Assert.ThrowsAsync<ProjectContextException>(
            () => factory.CreateAsync("/workspace/private"));

        Assert.Equal(ProjectContextErrorCode.PathAccessDenied, exception.ErrorCode);
    }

    [Fact]
    public async Task Run_context_uses_the_same_git_aware_canonical_policy()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/repository/src/service")
            .AddDirectory("/workspace/repository")
            .AddDirectory("/workspace/repository/src/service");
        var git = new FakeGitService
        {
            Repository = new GitRepositoryInfo(
                "/workspace/repository",
                "/workspace/repository/.git",
                "/workspace/repository/.git",
                false),
        };
        var factory = CreateFactory(fileSystem, git);

        var context = await factory.CreateForRunAsync(".");

        Assert.Equal("/workspace/repository/src/service", context.CurrentDirectory);
        Assert.Equal("/workspace/repository", context.ProjectRoot);
        Assert.True(context.IsGitRepository);
        Assert.Equal("/workspace/repository", context.GitWorktree);
        Assert.Equal(1, git.AvailabilityCallCount);
        Assert.Equal(1, git.DiscoverCallCount);
    }

    [Fact]
    public async Task Non_git_directory_builds_valid_context()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var git = new FakeGitService { IsAvailable = true };
        var factory = CreateFactory(fileSystem, git);

        var context = await factory.CreateAsync(null);

        Assert.False(context.IsGitRepository);
        Assert.Equal(context.CurrentDirectory, context.ProjectRoot);
        Assert.Null(context.GitWorktree);
        Assert.NotEqual(Guid.Empty, context.ProjectId.Value);
    }

    [Fact]
    public async Task Missing_git_executable_builds_non_git_context()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var git = new FakeGitService { IsAvailable = false };
        var factory = CreateFactory(fileSystem, git);

        var context = await factory.CreateAsync(null);

        Assert.False(context.IsGitRepository);
        Assert.Equal(0, git.DiscoverCallCount);
    }

    [Fact]
    public async Task Repository_subdirectory_resolves_to_current_working_tree_root()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/repository/src/service")
            .AddDirectory("/workspace/repository")
            .AddDirectory("/workspace/repository/src/service");
        var git = new FakeGitService
        {
            Repository = new GitRepositoryInfo(
                "/workspace/repository",
                "/workspace/repository/.git",
                "/workspace/repository/.git",
                false),
        };
        var factory = CreateFactory(fileSystem, git);

        var context = await factory.CreateAsync(null);

        Assert.Equal("/workspace/repository/src/service", context.CurrentDirectory);
        Assert.Equal("/workspace/repository", context.ProjectRoot);
        Assert.True(context.IsGitRepository);
        Assert.Equal("/workspace/repository", context.GitWorktree);
    }

    [Fact]
    public async Task Invalid_git_output_falls_back_to_non_git_context()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project")
            .AddDirectory("/other/repository");
        var git = new FakeGitService
        {
            Repository = new GitRepositoryInfo(
                "/other/repository",
                "/other/repository/.git",
                "/other/repository/.git",
                false),
        };
        var factory = CreateFactory(fileSystem, git);

        var context = await factory.CreateAsync(null);

        Assert.False(context.IsGitRepository);
        Assert.Equal("/workspace/project", context.ProjectRoot);
    }

    [Fact]
    public async Task Project_id_is_stable_across_repeated_runs()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var factory = CreateFactory(fileSystem);

        var first = await factory.CreateAsync(null);
        var second = await factory.CreateAsync(null);

        Assert.Equal(first.ProjectId, second.ProjectId);
    }

    [Fact]
    public async Task Repository_subdirectories_share_project_id()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/repository")
            .AddDirectory("/workspace/repository")
            .AddDirectory("/workspace/repository/src")
            .AddDirectory("/workspace/repository/tests");
        var git = new FakeGitService
        {
            Repository = new GitRepositoryInfo(
                "/workspace/repository",
                "/workspace/repository/.git",
                "/workspace/repository/.git",
                false),
        };
        var factory = CreateFactory(fileSystem, git);

        var first = await factory.CreateAsync("/workspace/repository/src");
        var second = await factory.CreateAsync("/workspace/repository/tests");

        Assert.Equal(first.ProjectId, second.ProjectId);
    }

    [Fact]
    public async Task Independent_worktree_roots_have_distinct_project_ids()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/main")
            .AddDirectory("/workspace/main")
            .AddDirectory("/workspace/feature");
        var git = new FakeGitService();
        var factory = CreateFactory(fileSystem, git);

        git.Repository = new GitRepositoryInfo(
            "/workspace/main",
            "/workspace/main/.git",
            "/workspace/main/.git",
            false);
        var main = await factory.CreateAsync("/workspace/main");

        git.Repository = new GitRepositoryInfo(
            "/workspace/feature",
            "/workspace/main/.git/worktrees/feature",
            "/workspace/main/.git",
            true);
        var worktree = await factory.CreateAsync("/workspace/feature");

        Assert.NotEqual(main.ProjectId, worktree.ProjectId);
    }

    [Fact]
    public async Task Current_date_is_derived_only_from_utc_clock()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var factory = CreateFactory(fileSystem);

        var context = await factory.CreateAsync(null);

        Assert.Equal(new DateTime(2026, 6, 27, 0, 0, 0, DateTimeKind.Utc), context.CurrentUtcDate);
        Assert.Equal(DateTimeKind.Utc, context.CurrentUtcDate.Kind);
    }

    [Fact]
    public async Task Non_utc_clock_is_rejected()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var factory = CreateFactory(
            fileSystem,
            clock: new FakeClock(DateTime.SpecifyKind(UtcNow, DateTimeKind.Local)));

        var exception = await Assert.ThrowsAsync<ProjectContextException>(
            () => factory.CreateAsync(null));

        Assert.Equal(ProjectContextErrorCode.InvalidUtcClock, exception.ErrorCode);
    }

    [Fact]
    public async Task Git_timeout_is_mapped_to_understandable_application_error()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var git = new FakeGitService
        {
            AvailabilityException = new ProcessTimeoutException("git", TimeSpan.FromSeconds(1)),
        };
        var factory = CreateFactory(fileSystem, git);

        var exception = await Assert.ThrowsAsync<ProjectContextException>(
            () => factory.CreateAsync(null));

        Assert.Equal(ProjectContextErrorCode.GitProcessTimedOut, exception.ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unexpected_git_process_failure_is_mapped_to_application_error(bool failsToStart)
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var innerException = new InvalidOperationException("process failure");
        var git = new FakeGitService
        {
            AvailabilityException = failsToStart
                ? new ProcessStartException("git", innerException)
                : new ProcessExecutionException("git", innerException),
        };
        var factory = CreateFactory(fileSystem, git);

        var exception = await Assert.ThrowsAsync<ProjectContextException>(
            () => factory.CreateAsync(null));

        Assert.Equal(ProjectContextErrorCode.GitProcessFailed, exception.ErrorCode);
        Assert.Same(git.AvailabilityException, exception.InnerException);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_timeout()
    {
        var fileSystem = new FakeProjectFileSystem("/workspace/project")
            .AddDirectory("/workspace/project");
        var git = new FakeGitService { WaitForCancellation = true };
        var factory = CreateFactory(fileSystem, git);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory.CreateAsync(null, cancellationSource.Token));
    }

    private static ProjectContextFactory CreateFactory(
        FakeProjectFileSystem fileSystem,
        FakeGitService? git = null,
        IClock? clock = null,
        ProjectPlatform platform = ProjectPlatform.Linux)
    {
        return new ProjectContextFactory(
            fileSystem,
            git ?? new FakeGitService(),
            clock ?? new FakeClock(UtcNow),
            new FakePlatformProvider(platform),
            new DeterministicProjectIdFactory());
    }

    private sealed class FakeClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class FakePlatformProvider(ProjectPlatform platform) : IPlatformProvider
    {
        public ProjectPlatform Current { get; } = platform;
    }

    private sealed class FakeGitService : IGitService
    {
        public bool IsAvailable { get; init; } = true;

        public GitRepositoryInfo? Repository { get; set; }

        public Exception? AvailabilityException { get; init; }

        public bool WaitForCancellation { get; init; }

        public int AvailabilityCallCount { get; private set; }

        public int DiscoverCallCount { get; private set; }

        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            AvailabilityCallCount++;

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (AvailabilityException is not null)
            {
                throw AvailabilityException;
            }

            return IsAvailable;
        }

        public Task<GitRepositoryInfo?> DiscoverAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            DiscoverCallCount++;
            return Task.FromResult(Repository);
        }
    }

    private sealed class FakeProjectFileSystem(string currentDirectory) : IProjectFileSystem
    {
        private readonly Dictionary<string, ProjectPathEntryKind> _entries =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _denied = new(StringComparer.Ordinal);

        public FakeProjectFileSystem AddDirectory(string path)
        {
            _entries[Normalize(path)] = ProjectPathEntryKind.Directory;
            return this;
        }

        public FakeProjectFileSystem AddFile(string path)
        {
            _entries[Normalize(path)] = ProjectPathEntryKind.File;
            return this;
        }

        public FakeProjectFileSystem Deny(string path)
        {
            _denied.Add(Normalize(path));
            return this;
        }

        public string GetCurrentDirectory() => Normalize(currentDirectory);

        public string NormalizePath(string path, string baseDirectory)
        {
            var combined = path.StartsWith("/", StringComparison.Ordinal)
                ? path
                : $"{baseDirectory.TrimEnd('/')}/{path}";
            return Normalize(combined);
        }

        public ProjectPathEntryKind GetEntryKind(string path)
        {
            var normalized = Normalize(path);
            if (_denied.Contains(normalized))
            {
                throw new ProjectContextException(
                    ProjectContextErrorCode.PathAccessDenied,
                    "Access denied.");
            }

            return _entries.GetValueOrDefault(normalized, ProjectPathEntryKind.Missing);
        }

        public string CanonicalizePath(string normalizedAbsolutePath, ProjectPlatform platform)
        {
            var canonical = Normalize(normalizedAbsolutePath);
            return platform == ProjectPlatform.Windows
                ? canonical.Replace('\\', '/').ToUpperInvariant()
                : canonical;
        }

        public bool IsPathWithin(
            string path,
            string candidateParent,
            ProjectPlatform platform)
        {
            var comparison = platform == ProjectPlatform.Windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var canonicalPath = CanonicalizePath(path, platform);
            var canonicalParent = CanonicalizePath(candidateParent, platform);

            return string.Equals(canonicalPath, canonicalParent, comparison) ||
                canonicalPath.StartsWith($"{canonicalParent.TrimEnd('/')}/", comparison);
        }

        private static string Normalize(string value)
        {
            var segments = new Stack<string>();
            foreach (var segment in value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.Pop();
                    }

                    continue;
                }

                segments.Push(segment);
            }

            return "/" + string.Join("/", segments.Reverse());
        }
    }
}
