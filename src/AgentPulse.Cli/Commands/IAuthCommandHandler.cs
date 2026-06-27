namespace AgentPulse.Cli.Commands;

public interface IAuthCommandHandler
{
    Task<int> HandleAsync(
        string subcommand,
        CancellationToken cancellationToken = default);
}
