using AgentPulse.Application.Persistence;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence.Repositories;

public sealed class RunLeaseRepository(AgentPulseDbContext dbContext) : IRunLeaseRepository
{
    public Task<RunLease?> GetBySessionIdAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.RunLeases.SingleOrDefaultAsync(
            runLease => runLease.SessionId == sessionId,
            cancellationToken);
    }

    public async Task AddAsync(
        RunLease runLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runLease);
        await dbContext.RunLeases.AddAsync(runLease, cancellationToken);
    }

    public Task<RunLeaseId?> GetLeaseIdAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.RunLeases
            .Where(runLease => runLease.SessionId == sessionId)
            .Select(runLease => (RunLeaseId?)runLease.LeaseId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> RemoveOwnedAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken = default)
    {
        var removed = await dbContext.RunLeases
            .Where(runLease =>
                runLease.SessionId == sessionId &&
                runLease.LeaseId == leaseId)
            .ExecuteDeleteAsync(cancellationToken);
        return removed == 1;
    }

    public void Remove(RunLease runLease)
    {
        ArgumentNullException.ThrowIfNull(runLease);
        dbContext.RunLeases.Remove(runLease);
    }
}
