using Microsoft.Extensions.Options;
using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Console;

namespace AgentPulse.Cli.Commands;

public sealed class CliApplication(
    IConsole console,
    IPromptInputReader promptInputReader,
    IRunCommandHandler runCommandHandler,
    IOptions<CliOptions> options)
{
    private const string EmptyPromptError = "You must provide a message or a command";

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            await WriteHelpAsync(cancellationToken);
            return ExitCodes.Success;
        }

        if (!string.Equals(arguments[0], "run", StringComparison.Ordinal))
        {
            await console.Error.WriteLineAsync(
                $"Unknown command: {arguments[0]}".AsMemory(),
                cancellationToken);
            return ExitCodes.Failure;
        }

        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            await WriteHelpAsync(cancellationToken);
            return ExitCodes.Success;
        }

        var promptArguments = arguments.Skip(1).ToArray();
        var prompt = await promptInputReader.ReadAsync(promptArguments, cancellationToken);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            await console.Error.WriteLineAsync(EmptyPromptError.AsMemory(), cancellationToken);
            await console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }

        return await runCommandHandler.HandleAsync(prompt, cancellationToken);
    }

    private static bool IsHelp(string argument)
    {
        return argument is "--help" or "-h";
    }

    private async Task WriteHelpAsync(CancellationToken cancellationToken)
    {
        var applicationName = options.Value.ApplicationName;
        var help = $$"""
            {{applicationName}} command-line interface

            Usage:
              {{applicationName}} --help
              {{applicationName}} run [message...]

            Commands:
              run    Receive a prompt from arguments and/or stdin.
            """;

        await console.Out.WriteLineAsync(help.AsMemory(), cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
    }
}
