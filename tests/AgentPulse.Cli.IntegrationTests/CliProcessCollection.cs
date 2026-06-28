namespace AgentPulse.Cli.IntegrationTests;

internal static class ProcessInterruptCollection
{
    public const string Name = "ProcessInterruptTests";
}

[CollectionDefinition(ProcessInterruptCollection.Name, DisableParallelization = true)]
public sealed class CliProcessCollection
{
}
