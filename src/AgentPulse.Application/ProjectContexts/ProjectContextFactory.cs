using AgentPulse.Application.Processes;

namespace AgentPulse.Application.ProjectContexts;

public sealed class ProjectContextFactory(
    IProjectFileSystem fileSystem,
    IGitService gitService,
    IClock clock,
    IPlatformProvider platformProvider,
    IProjectIdFactory projectIdFactory) : IProjectContextFactory
{
    public Task<ProjectContext> CreateAsync(
        string? inputPath,
        CancellationToken cancellationToken = default)
    {
        return CreateCoreAsync(inputPath, discoverGit: true, cancellationToken);
    }

    public Task<ProjectContext> CreateForRunAsync(
        string? inputPath,
        CancellationToken cancellationToken = default)
    {
        return CreateAsync(inputPath, cancellationToken);
    }

    private async Task<ProjectContext> CreateCoreAsync(
        string? inputPath,
        bool discoverGit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var platform = platformProvider.Current;
        var currentDirectory = ResolveInputDirectory(inputPath);
        ValidateDirectory(currentDirectory);

        var projectRoot = currentDirectory;
        var isGitRepository = false;
        string? gitWorktree = null;

        if (discoverGit)
        {
            try
            {
                if (await gitService.IsAvailableAsync(cancellationToken))
                {
                    var repository = await gitService.DiscoverAsync(
                        currentDirectory,
                        cancellationToken);
                    var discoveredRoot = TryResolveRepositoryRoot(
                        repository,
                        currentDirectory,
                        platform);

                    if (discoveredRoot is not null)
                    {
                        projectRoot = discoveredRoot;
                        isGitRepository = true;
                        gitWorktree = discoveredRoot;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProcessTimeoutException exception)
            {
                throw new ProjectContextException(
                    ProjectContextErrorCode.GitProcessTimedOut,
                    "Git discovery timed out while building the project context.",
                    exception);
            }
            catch (Exception exception) when (
                exception is ProcessStartException or ProcessExecutionException)
            {
                throw new ProjectContextException(
                    ProjectContextErrorCode.GitProcessFailed,
                    "Git failed while building the project context.",
                    exception);
            }
        }

        var utcNow = clock.UtcNow;
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ProjectContextException(
                ProjectContextErrorCode.InvalidUtcClock,
                "The configured clock returned a non-UTC timestamp.");
        }

        var currentUtcDate = utcNow.Date;
        var canonicalProjectRoot = fileSystem.CanonicalizePath(projectRoot, platform);
        var projectId = projectIdFactory.Create(canonicalProjectRoot, platform);

        return new ProjectContext(
            currentDirectory,
            discoverGit ? projectRoot : canonicalProjectRoot,
            isGitRepository,
            gitWorktree,
            platform,
            currentUtcDate,
            projectId);
    }

    private string ResolveInputDirectory(string? inputPath)
    {
        try
        {
            var processDirectory = fileSystem.GetCurrentDirectory();
            var path = string.IsNullOrWhiteSpace(inputPath) ? processDirectory : inputPath;
            return fileSystem.NormalizePath(path, processDirectory);
        }
        catch (ProjectContextException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectContextException(
                ProjectContextErrorCode.InvalidPath,
                "The project path is invalid.",
                exception);
        }
    }

    private void ValidateDirectory(string path)
    {
        ProjectPathEntryKind entryKind;

        try
        {
            entryKind = fileSystem.GetEntryKind(path);
        }
        catch (ProjectContextException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ProjectContextException(
                ProjectContextErrorCode.PathAccessDenied,
                $"The project path '{path}' cannot be accessed.",
                exception);
        }

        if (entryKind == ProjectPathEntryKind.Missing)
        {
            throw new ProjectContextException(
                ProjectContextErrorCode.PathNotFound,
                $"The project path '{path}' does not exist.");
        }

        if (entryKind != ProjectPathEntryKind.Directory)
        {
            throw new ProjectContextException(
                ProjectContextErrorCode.PathIsNotDirectory,
                $"The project path '{path}' is not a directory.");
        }
    }

    private string? TryResolveRepositoryRoot(
        GitRepositoryInfo? repository,
        string currentDirectory,
        ProjectPlatform platform)
    {
        if (repository is null || string.IsNullOrWhiteSpace(repository.WorkingTreeRoot))
        {
            return null;
        }

        try
        {
            var root = fileSystem.NormalizePath(repository.WorkingTreeRoot, currentDirectory);

            if (fileSystem.GetEntryKind(root) != ProjectPathEntryKind.Directory)
            {
                return null;
            }

            return fileSystem.IsPathWithin(currentDirectory, root, platform) ? root : null;
        }
        catch (ProjectContextException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
