namespace AgentPulse.Application.ProjectContexts;

public interface IGitService
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<GitRepositoryInfo?> DiscoverAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
