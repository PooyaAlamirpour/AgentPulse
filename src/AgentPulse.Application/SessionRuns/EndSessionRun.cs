using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public sealed class EndSessionRun(
    ISessionRepository sessionRepository,
    IRunLeaseRepository runLeaseRepository,
    IUnitOfWork unitOfWork,
    IClock clock) : IEndSessionRun
{
    public async Task ExecuteAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken = default)
    {
        var utcNow = SessionRunTime.GetUtcNow(clock);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.SessionNotFound,
                $"Session '{sessionId}' does not exist or is no longer available.");

        var released = await runLeaseRepository.RemoveOwnedAsync(
            sessionId,
            leaseId,
            cancellationToken);

        if (!released)
        {
            var currentOwner = await runLeaseRepository.GetLeaseIdAsync(
                sessionId,
                cancellationToken);

            if (currentOwner is not null)
            {
                throw new SessionRunException(
                    SessionRunErrorCode.RunLeaseOwnershipMismatch,
                    "The run lease can only be released by its owner.");
            }

            if (session.Status == SessionStatus.Idle)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            throw new SessionRunException(
                SessionRunErrorCode.RunLeaseNotFound,
                $"Session '{sessionId}' has no active run lease.");
        }

        if (session.Status == SessionStatus.Running)
        {
            session.Stop(utcNow);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
