namespace AgentPulse.Application.ProjectContexts;

public interface IProjectContextFactory
{
    Task<ProjectContext> CreateAsync(
        string? inputPath,
        CancellationToken cancellationToken = default);
}
