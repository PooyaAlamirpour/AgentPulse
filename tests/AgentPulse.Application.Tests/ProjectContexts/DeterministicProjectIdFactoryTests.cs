using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Application.Tests.ProjectContexts;

public sealed class DeterministicProjectIdFactoryTests
{
    [Fact]
    public void Same_canonical_path_always_produces_same_identifier()
    {
        var factory = new DeterministicProjectIdFactory();

        var first = factory.Create("/workspace/project", ProjectPlatform.Linux);
        var second = factory.Create("/workspace/project", ProjectPlatform.Linux);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Canonical_root_and_platform_are_both_part_of_the_identifier()
    {
        var factory = new DeterministicProjectIdFactory();

        var firstRoot = factory.Create("/workspace/project", ProjectPlatform.Linux);
        var secondRoot = factory.Create("/workspace/other", ProjectPlatform.Linux);
        var otherPlatform = factory.Create("/workspace/project", ProjectPlatform.MacOs);

        Assert.NotEqual(firstRoot, secondRoot);
        Assert.NotEqual(firstRoot, otherPlatform);
    }
}
