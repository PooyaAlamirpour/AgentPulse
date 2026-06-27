using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public interface IRenewSessionRunLease
{
    Task<RunLease> ExecuteAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken = default);
}
