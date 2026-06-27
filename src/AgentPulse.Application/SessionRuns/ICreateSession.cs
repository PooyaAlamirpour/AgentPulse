using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public interface ICreateSession
{
    Task<Session> ExecuteAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);
}
