using AgentPulse.Domain.Projects;

namespace AgentPulse.Application.ProjectContexts;

public interface IProjectIdFactory
{
    ProjectId Create(string canonicalProjectRoot, ProjectPlatform platform);
}
