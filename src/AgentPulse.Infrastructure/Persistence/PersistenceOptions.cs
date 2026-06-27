namespace AgentPulse.Infrastructure.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "AgentPulse:Persistence";

    public string? DatabasePath { get; set; }
}
