using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public interface IEndSessionRun
{
    Task ExecuteAsync(
        SessionId sessionId,
        RunLeaseId leaseId,
        CancellationToken cancellationToken = default);
}
