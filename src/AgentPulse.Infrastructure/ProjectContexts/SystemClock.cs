using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Infrastructure.ProjectContexts;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
