using AgentPulse.Domain;

namespace AgentPulse.Domain.Tests;

public sealed class DomainAssemblyTests
{
    [Fact]
    public void Domain_assembly_has_expected_name()
    {
        Assert.Equal("AgentPulse.Domain", DomainAssembly.Value.GetName().Name);
    }
}
