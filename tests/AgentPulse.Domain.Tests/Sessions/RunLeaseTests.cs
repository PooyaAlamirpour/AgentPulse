using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Domain.Tests.Sessions;

public sealed class RunLeaseTests
{
    [Fact]
    public void Lease_requires_utc_ordered_timestamps_and_owner_for_renewal()
    {
        var acquiredAtUtc = new DateTime(2026, 6, 27, 10, 0, 0, DateTimeKind.Utc);
        var leaseId = RunLeaseId.New();
        var lease = new RunLease(
            SessionId.New(),
            leaseId,
            acquiredAtUtc,
            acquiredAtUtc.AddMinutes(5));

        Assert.False(lease.IsExpired(acquiredAtUtc.AddMinutes(4)));
        Assert.True(lease.IsExpired(acquiredAtUtc.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(() => lease.Renew(
            RunLeaseId.New(),
            acquiredAtUtc.AddMinutes(1),
            acquiredAtUtc.AddMinutes(6)));
    }
}
