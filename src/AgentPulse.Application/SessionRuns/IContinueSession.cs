using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public interface IContinueSession
{
    Task<Session> ExecuteAsync(
        ProjectId projectId,
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}
