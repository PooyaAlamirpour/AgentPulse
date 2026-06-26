using AgentPulse.Application;

namespace AgentPulse.Application.Tests;

public sealed class ApplicationAssemblyTests
{
    [Fact]
    public void Application_assembly_has_expected_name()
    {
        Assert.Equal("AgentPulse.Application", ApplicationAssembly.Value.GetName().Name);
    }
}
