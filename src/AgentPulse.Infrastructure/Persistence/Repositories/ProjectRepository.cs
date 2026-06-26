using AgentPulse.Application.Persistence;
using AgentPulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence.Repositories;

public sealed class ProjectRepository(AgentPulseDbContext dbContext) : IProjectRepository
{
    public Task<Project?> GetByIdAsync(
        ProjectId id,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Projects.SingleOrDefaultAsync(
            project => project.Id == id,
            cancellationToken);
    }

    public Task<Project?> GetByNormalizedRootPathAsync(
        string normalizedRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRootPath);

        return dbContext.Projects.SingleOrDefaultAsync(
            project => project.NormalizedRootPath == normalizedRootPath,
            cancellationToken);
    }

    public async Task AddAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        await dbContext.Projects.AddAsync(project, cancellationToken);
    }

    public void Remove(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        dbContext.Projects.Remove(project);
    }
}
