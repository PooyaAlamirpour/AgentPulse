using AgentPulse.Domain.Projects;

namespace AgentPulse.Application.Persistence;

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(ProjectId id, CancellationToken cancellationToken = default);

    Task<Project?> GetByNormalizedRootPathAsync(
        string normalizedRootPath,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(Project project, CancellationToken cancellationToken = default);

    Task AddAsync(Project project, CancellationToken cancellationToken = default);

    void Remove(Project project);
}
