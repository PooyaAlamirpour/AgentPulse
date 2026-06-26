using AgentPulse.Application.Persistence;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence.Repositories;

public sealed class SessionRepository(AgentPulseDbContext dbContext) : ISessionRepository
{
    public Task<Session?> GetByIdAsync(
        SessionId id,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Sessions.SingleOrDefaultAsync(
            session => session.Id == id,
            cancellationToken);
    }

    public async Task<IReadOnlyList<Session>> ListByProjectIdAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Sessions
            .Where(session => session.ProjectId == projectId)
            .OrderBy(session => session.CreatedAtUtc)
            .ThenBy(session => session.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await dbContext.Sessions.AddAsync(session, cancellationToken);
    }

    public void Remove(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        dbContext.Sessions.Remove(session);
    }
}
