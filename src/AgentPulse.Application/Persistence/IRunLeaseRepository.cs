using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Persistence;

public interface IRunLeaseRepository
{
    Task<RunLease?> GetBySessionIdAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task AddAsync(RunLease runLease, CancellationToken cancellationToken = default);

    void Remove(RunLease runLease);
}
