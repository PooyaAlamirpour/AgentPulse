using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public sealed class RenewSessionRunLease(
    IRunLeaseRepository runLeaseRepository,
    IUnitOfWork unitOfWork,
    IClock clock,
    SessionRunOptions options) : IRenewSessionRunLease
{
    public async Task<RunLease> ExecuteAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var utcNow = SessionRunTime.GetUtcNow(clock);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var runLease = await runLeaseRepository.GetBySessionIdAsync(
            sessionId,
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
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return runLease;
    }
}
