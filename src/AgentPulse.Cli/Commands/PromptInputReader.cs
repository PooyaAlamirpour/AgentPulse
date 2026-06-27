using AgentPulse.Cli.Console;

namespace AgentPulse.Cli.Commands;

public sealed class PromptInputReader(IConsole console) : IPromptInputReader
{
    public async Task<string?> ReadAsync(
        string? positionalPrompt,
        CancellationToken cancellationToken)
    {
        if (positionalPrompt is not null)
        {
            return positionalPrompt;
        }

        if (!console.IsInputRedirected)
        {
            return null;
        }

        var prompt = await console.In.ReadToEndAsync(cancellationToken);
        return prompt.TrimEnd('\r', '\n');
    }
}
