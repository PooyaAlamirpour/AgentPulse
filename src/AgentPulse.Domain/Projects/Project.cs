using AgentPulse.Domain.Common;

namespace AgentPulse.Domain.Projects;

public sealed class Project
{
    private Project()
    {
        NormalizedRootPath = null!;
    }

    public Project(
        ProjectId id,
        string normalizedRootPath,
        bool isGitRepository,
        string? gitWorktree,
        DateTime createdAtUtc)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Project identifier cannot be empty.", nameof(id));
        }

        Id = id;
        NormalizedRootPath = RequirePath(normalizedRootPath, nameof(normalizedRootPath));
        IsGitRepository = isGitRepository;
        GitWorktree = NormalizeGitWorktree(isGitRepository, gitWorktree);
        CreatedAtUtc = UtcDateTime.Ensure(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public ProjectId Id { get; private set; }

    public string NormalizedRootPath { get; private set; }

    public bool IsGitRepository { get; private set; }

    public string? GitWorktree { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public void UpdateLocation(
        string normalizedRootPath,
        bool isGitRepository,
        string? gitWorktree,
        DateTime updatedAtUtc)
    {
        var validatedRootPath = RequirePath(normalizedRootPath, nameof(normalizedRootPath));
        var validatedGitWorktree = NormalizeGitWorktree(isGitRepository, gitWorktree);
        var validatedUpdatedAtUtc = UtcDateTime.EnsureNotBefore(
            updatedAtUtc,
            CreatedAtUtc,
            nameof(updatedAtUtc));

        NormalizedRootPath = validatedRootPath;
        IsGitRepository = isGitRepository;
        GitWorktree = validatedGitWorktree;
        UpdatedAtUtc = validatedUpdatedAtUtc;
    }

    private static string RequirePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string? NormalizeGitWorktree(bool isGitRepository, string? gitWorktree)
    {
        if (!isGitRepository)
        {
            if (!string.IsNullOrWhiteSpace(gitWorktree))
            {
                throw new ArgumentException(
                    "A non-Git project cannot have a Git worktree.",
                    nameof(gitWorktree));
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(gitWorktree))
        {
            throw new ArgumentException(
                "A Git project must have a Git worktree.",
                nameof(gitWorktree));
        }

        return gitWorktree.Trim();
    }
}
