using System.Reflection;

namespace AgentPulse.Domain;

public static class DomainAssembly
{
    public static Assembly Value => typeof(DomainAssembly).Assembly;
}
