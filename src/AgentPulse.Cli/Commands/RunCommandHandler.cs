using AgentPulse.Cli.Console;

namespace AgentPulse.Cli.Commands;

public sealed class RunCommandHandler(IConsole console) : IRunCommandHandler
{
    public async Task<int> HandleAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        await console.Out.WriteLineAsync(prompt.AsMemory(), cancellationToken);
        await console.Out.FlushAsync(cancellationToken);

        return ExitCodes.Success;
    }
}
