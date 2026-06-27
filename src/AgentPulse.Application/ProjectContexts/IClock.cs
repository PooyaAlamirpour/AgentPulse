namespace AgentPulse.Application.ProjectContexts;

public interface IClock
{
    DateTime UtcNow { get; }
}
