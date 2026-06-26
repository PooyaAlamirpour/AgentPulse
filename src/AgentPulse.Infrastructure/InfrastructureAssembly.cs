using System.Reflection;

namespace AgentPulse.Infrastructure;

public static class InfrastructureAssembly
{
    public static Assembly Value => typeof(InfrastructureAssembly).Assembly;
}
