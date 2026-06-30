namespace AgentPulse.Infrastructure.Mutations;

internal interface IPathLockManager
{
    Task<IAsyncDisposable> AcquireAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken);
}
