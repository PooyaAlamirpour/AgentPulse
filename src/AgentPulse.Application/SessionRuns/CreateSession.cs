using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public sealed class CreateSession(
    IProjectRepository projectRepository,
    ISessionRepository sessionRepository,
    IUnitOfWork unitOfWork,
    IClock clock) : ICreateSession
{
    public async Task<Session> ExecuteAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        _ = await projectRepository.GetByIdAsync(projectId, cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.ProjectNotFound,
                $"Project '{projectId}' does not exist.");

        var session = new Session(SessionId.New(), projectId, SessionRunTime.GetUtcNow(clock));
        await sessionRepository.AddAsync(session, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }
}
