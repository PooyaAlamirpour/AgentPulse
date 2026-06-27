using AgentPulse.Application.Persistence;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Projects;

namespace AgentPulse.Application.SessionRuns;

public sealed class RegisterProject(
    IProjectRepository projectRepository,
    IUnitOfWork unitOfWork,
    IClock clock) : IRegisterProject
{
    public async Task<Project> ExecuteAsync(
        ProjectContext projectContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectContext);
        var utcNow = SessionRunTime.GetUtcNow(clock);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var project = await UpsertAndLoadAsync(projectContext, utcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return project;
    }

    internal async Task<Project> UpsertAndLoadAsync(
        ProjectContext projectContext,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var candidate = new Project(
            projectContext.ProjectId,
            projectContext.ProjectRoot,
            projectContext.IsGitRepository,
            projectContext.GitWorktree,
            utcNow);

        await projectRepository.UpsertAsync(candidate, cancellationToken);

        return await projectRepository.GetByIdAsync(projectContext.ProjectId, cancellationToken)
            ?? throw new SessionRunException(
                SessionRunErrorCode.ProjectNotFound,
                $"Project '{projectContext.ProjectId}' could not be loaded after registration.");
    }
}
