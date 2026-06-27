using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.ModelRuns;

public interface IRunLeaseRenewalService
{
    Task RenewAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken = default);
}
