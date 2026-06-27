using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Projects;

namespace AgentPulse.Application.SessionRuns;

public interface IRegisterProject
{
    Task<Project> ExecuteAsync(
        ProjectContext projectContext,
        CancellationToken cancellationToken = default);
}
