using AgentPulse.Domain.Projects;

namespace AgentPulse.Application.ProjectContexts;

public sealed record ProjectContext
{
    public ProjectContext(
        string currentDirectory,
        string projectRoot,
        bool isGitRepository,
        string? gitWorktree,
        ProjectPlatform platform,
        DateTime currentUtcDate,
        ProjectId projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        if (currentUtcDate.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Current UTC date must use DateTimeKind.Utc.", nameof(currentUtcDate));
        }

        if (isGitRepository && string.IsNullOrWhiteSpace(gitWorktree))
        {
            throw new ArgumentException(
                "A Git project context must include its working tree root.",
                nameof(gitWorktree));
        }

        if (!isGitRepository && gitWorktree is not null)
        {
            throw new ArgumentException(
                "A non-Git project context cannot include a Git working tree.",
                nameof(gitWorktree));
        }

        CurrentDirectory = currentDirectory;
        ProjectRoot = projectRoot;
        IsGitRepository = isGitRepository;
        GitWorktree = gitWorktree;
        Platform = platform;
        CurrentUtcDate = currentUtcDate;
        ProjectId = projectId;
    }

    public string CurrentDirectory { get; }

    public string ProjectRoot { get; }

    public bool IsGitRepository { get; }

    public string? GitWorktree { get; }

    public ProjectPlatform Platform { get; }

    public DateTime CurrentUtcDate { get; }

    public ProjectId ProjectId { get; }
}
