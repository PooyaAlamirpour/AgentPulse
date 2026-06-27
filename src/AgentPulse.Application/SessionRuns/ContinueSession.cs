using AgentPulse.Application.Persistence;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public sealed class ContinueSession(ISessionRepository sessionRepository) : IContinueSession
{
    public async Task<Session> ExecuteAsync(
        ProjectId projectId,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.SessionNotFound,
                $"Session '{sessionId}' does not exist or is no longer available.");

        if (session.ProjectId != projectId)
        {
            throw new SessionRunException(
                SessionRunErrorCode.SessionProjectMismatch,
                $"Session '{sessionId}' does not belong to project '{projectId}'.");
        }

        return session;
    }
}
