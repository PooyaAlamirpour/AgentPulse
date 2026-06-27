namespace AgentPulse.Cli.Commands;

public interface IRunCommandHandler
{
    Task<int> HandleAsync(
        RunCommandOptions options,
        CancellationToken cancellationToken);
}
