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

    public async Task UpsertAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var trackedProject = dbContext.ChangeTracker
            .Entries<Project>()
            .SingleOrDefault(entry => entry.Entity.Id == project.Id);
        if (trackedProject is not null)
        {
            trackedProject.State = EntityState.Detached;
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Projects
                (Id, NormalizedRootPath, IsGitRepository, GitWorktree, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ({project.Id.Value}, {project.NormalizedRootPath}, {project.IsGitRepository},
                 {project.GitWorktree}, {project.CreatedAtUtc.Ticks}, {project.UpdatedAtUtc.Ticks})
            ON CONFLICT(Id) DO UPDATE SET
                NormalizedRootPath = excluded.NormalizedRootPath,
                IsGitRepository = excluded.IsGitRepository,
                GitWorktree = excluded.GitWorktree,
                UpdatedAtUtc = MAX(Projects.UpdatedAtUtc, excluded.UpdatedAtUtc);
            """, cancellationToken);
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
