using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Application.SessionRuns;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence;

public sealed class RunLeaseRenewalService(
    IDbContextFactory<AgentPulseDbContext> dbContextFactory,
    IClock clock,
    SessionRunOptions options) : IRunLeaseRenewalService
{
    public async Task RenewAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var utcNow = GetUtcNow();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            cancellationToken);

        var runLease = await dbContext.RunLeases.SingleOrDefaultAsync(
                value => value.SessionId == sessionId,
                cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.RunLeaseNotFound,
                $"Session '{sessionId}' has no active run lease.");

        if (runLease.LeaseId != leaseId)
        {
            throw new SessionRunException(
                SessionRunErrorCode.RunLeaseOwnershipMismatch,
                "The run lease can only be renewed by its owner.");
        }

        if (runLease.IsExpired(utcNow))
        {
            throw new SessionRunException(
                SessionRunErrorCode.RunLeaseExpired,
                "An expired run lease cannot be renewed.");
        }

        runLease.Renew(leaseId, utcNow, utcNow.Add(options.LeaseDuration));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private DateTime GetUtcNow()
    {
        var utcNow = clock.UtcNow;
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new SessionRunException(
                SessionRunErrorCode.InvalidUtcClock,
                "The configured clock returned a non-UTC timestamp.");
        }

        return utcNow;
    }
}
