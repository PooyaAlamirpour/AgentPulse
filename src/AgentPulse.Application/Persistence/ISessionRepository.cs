using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Persistence;

public interface ISessionRepository
{
    Task<Session?> GetByIdAsync(SessionId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Session>> ListByProjectIdAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task AddAsync(Session session, CancellationToken cancellationToken = default);

    void Remove(Session session);
}
