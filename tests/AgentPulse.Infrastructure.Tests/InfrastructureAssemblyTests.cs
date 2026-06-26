using AgentPulse.Infrastructure;

namespace AgentPulse.Infrastructure.Tests;

public sealed class InfrastructureAssemblyTests
{
    [Fact]
    public void Infrastructure_assembly_has_expected_name()
    {
        Assert.Equal("AgentPulse.Infrastructure", InfrastructureAssembly.Value.GetName().Name);
    }
}
