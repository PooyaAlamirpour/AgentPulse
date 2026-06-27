using AgentPulse.Domain.Common;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Domain.SessionRuns;

public sealed class RunLease
{
    private RunLease()
    {
    }

    public RunLease(
        SessionId sessionId,
        RunLeaseId leaseId,
        DateTime acquiredAtUtc,
        DateTime expiresAtUtc)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Session identifier cannot be empty.", nameof(sessionId));
        }

        if (leaseId.Value == Guid.Empty)
        {
            throw new ArgumentException("Run lease identifier cannot be empty.", nameof(leaseId));
        }

        SessionId = sessionId;
        LeaseId = leaseId;
        AcquiredAtUtc = UtcDateTime.Ensure(acquiredAtUtc, nameof(acquiredAtUtc));
        ExpiresAtUtc = EnsureExpiration(expiresAtUtc, AcquiredAtUtc, nameof(expiresAtUtc));
    }

    public SessionId SessionId { get; private set; }

    public RunLeaseId LeaseId { get; private set; }

    public DateTime AcquiredAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public bool IsExpired(DateTime utcNow)
    {
        var validatedUtcNow = UtcDateTime.Ensure(utcNow, nameof(utcNow));
        return ExpiresAtUtc <= validatedUtcNow;
    }

    public void Renew(
        RunLeaseId ownerLeaseId,
        DateTime renewedAtUtc,
        DateTime expiresAtUtc)
    {
        EnsureOwner(ownerLeaseId);
        var validatedRenewedAtUtc = UtcDateTime.Ensure(renewedAtUtc, nameof(renewedAtUtc));

        if (IsExpired(validatedRenewedAtUtc))
        {
            throw new InvalidOperationException("An expired run lease cannot be renewed.");
        }

        ExpiresAtUtc = EnsureExpiration(
            expiresAtUtc,
            validatedRenewedAtUtc,
            nameof(expiresAtUtc));
    }

    public void EnsureOwner(RunLeaseId ownerLeaseId)
    {
        if (LeaseId != ownerLeaseId)
        {
            throw new InvalidOperationException("The run lease is owned by another caller.");
        }
    }

    private static DateTime EnsureExpiration(
        DateTime expiresAtUtc,
        DateTime minimumExclusiveUtc,
        string parameterName)
    {
        var validatedExpiresAtUtc = UtcDateTime.Ensure(expiresAtUtc, parameterName);

        if (validatedExpiresAtUtc <= minimumExclusiveUtc)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                expiresAtUtc,
                "Run lease expiration must be later than its acquisition or renewal time.");
        }

        return validatedExpiresAtUtc;
    }
}
