namespace AgentPulse.Application.ProjectContexts;

public interface IPlatformProvider
{
    ProjectPlatform Current { get; }
}
