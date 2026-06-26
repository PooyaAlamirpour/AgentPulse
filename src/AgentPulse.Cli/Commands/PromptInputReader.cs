using AgentPulse.Cli.Console;

namespace AgentPulse.Cli.Commands;

public sealed class PromptInputReader(IConsole console) : IPromptInputReader
{
    public async Task<string> ReadAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var promptFromArguments = string.Join(
            " ",
            arguments
                .Where(static argument => argument != "--")
                .Select(FormatArgument));

        if (!console.IsInputRedirected)
        {
            return promptFromArguments;
        }

        var readStandardInputTask = Task.Run(
            () => console.In.ReadToEnd(),
            CancellationToken.None);
        var promptFromStandardInput = await readStandardInputTask
            .WaitAsync(cancellationToken);
        return string.Concat(promptFromArguments, "\n", promptFromStandardInput);
    }

    private static string FormatArgument(string argument)
    {
        return argument.Contains(' ')
            ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;
    }
}
