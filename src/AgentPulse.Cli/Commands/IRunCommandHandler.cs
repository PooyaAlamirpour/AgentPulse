namespace AgentPulse.Cli.Commands;

public interface IRunCommandHandler
{
    Task<int> HandleAsync(string prompt, CancellationToken cancellationToken);
}
