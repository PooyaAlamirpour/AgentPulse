namespace AgentPulse.Cli.Commands;

public interface IPromptInputReader
{
    Task<string> ReadAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
