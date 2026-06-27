namespace AgentPulse.Application.ProjectContexts;

public interface IProjectContextFactory
{
    Task<ProjectContext> CreateAsync(
        string? inputPath,
        CancellationToken cancellationToken = default);

    Task<ProjectContext> CreateForRunAsync(
        string? inputPath,
        CancellationToken cancellationToken = default)
    {
        return CreateAsync(inputPath, cancellationToken);
    }
}
