namespace AgentPulse.Cli.Commands;

public interface ICliErrorRenderer
{
    Task<int> RenderAsync(
        Exception exception,
        CancellationToken cancellationToken);

    Task<int> RenderUsageAsync(
        string message,
        CancellationToken cancellationToken);

    Task<int> RenderConfigurationAsync(
        string message,
        CancellationToken cancellationToken);

    Task<int> RenderCancellationAsync(CancellationToken cancellationToken);
}
