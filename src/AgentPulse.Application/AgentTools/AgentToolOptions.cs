namespace AgentPulse.Application.AgentTools;

public sealed class AgentToolOptions
{
    public const string SectionName = "AgentPulse:Tools";

    public int MaxToolIterations { get; set; } = 8;

    public int MaxOutputCharacters { get; set; } = 30_000;

    public int MaxGlobResults { get; set; } = 200;

    public int MaxGrepResults { get; set; } = 100;

    public int MaxReadLines { get; set; } = 500;

    public long MaxReadableFileBytes { get; set; } = 5 * 1024 * 1024;

    public long MaxGrepFileBytes { get; set; } = 2 * 1024 * 1024;

    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaxToolIterations <= 0)
        {
            throw new InvalidOperationException("Max tool iterations must be greater than zero.");
        }

        if (MaxOutputCharacters <= 0 || MaxGlobResults <= 0 || MaxGrepResults <= 0 ||
            MaxReadLines <= 0 || MaxReadableFileBytes <= 0 || MaxGrepFileBytes <= 0)
        {
            throw new InvalidOperationException("Tool limits must be greater than zero.");
        }

        if (ToolTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Tool timeout must be positive.");
        }
    }
}
