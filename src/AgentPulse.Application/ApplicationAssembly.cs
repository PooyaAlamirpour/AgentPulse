using System.Reflection;

namespace AgentPulse.Application;

public static class ApplicationAssembly
{
    public static Assembly Value => typeof(ApplicationAssembly).Assembly;
}
