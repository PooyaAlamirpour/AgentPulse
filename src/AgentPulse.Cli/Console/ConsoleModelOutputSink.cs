using AgentPulse.Application.ModelRuns;

namespace AgentPulse.Cli.Console;

public sealed class ConsoleModelOutputSink(IConsole console) : IModelOutputSink
{
    public async Task WriteDeltaAsync(
        string delta,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(delta);
        await console.Out.WriteAsync(delta.AsMemory(), cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await console.Out.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
    }
}
