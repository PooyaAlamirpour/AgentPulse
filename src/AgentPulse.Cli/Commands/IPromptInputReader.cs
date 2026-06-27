namespace AgentPulse.Cli.Commands;

public interface IPromptInputReader
{
    Task<string?> ReadAsync(
        string? positionalPrompt,
        CancellationToken cancellationToken);
}
