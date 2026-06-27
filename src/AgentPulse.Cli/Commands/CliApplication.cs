using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Console;
using Microsoft.Extensions.Options;

namespace AgentPulse.Cli.Commands;

public sealed class CliApplication(
    IConsole console,
    IPromptInputReader promptInputReader,
    IRunCommandHandler runCommandHandler,
    IAuthCommandHandler authCommandHandler,
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

        if (string.Equals(arguments[0], "auth", StringComparison.Ordinal))
        {
            return await RunAuthAsync(arguments, cancellationToken);
        }

        if (!string.Equals(arguments[0], "run", StringComparison.Ordinal))
        {
            await console.Error.WriteLineAsync(
                $"Unknown command: {arguments[0]}".AsMemory(),
                cancellationToken);
            await console.Error.FlushAsync(cancellationToken);
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

    private async Task<int> RunAuthAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 1 ||
            (arguments.Count == 2 && IsHelp(arguments[1])))
        {
            await WriteAuthHelpAsync(cancellationToken);
            return ExitCodes.Success;
        }

        if (arguments.Count != 2)
        {
            await console.Error.WriteLineAsync(
                "Auth commands do not accept additional arguments.".AsMemory(),
                cancellationToken);
            await console.Error.FlushAsync(cancellationToken);
            return ExitCodes.Failure;
        }

        return await authCommandHandler.HandleAsync(arguments[1], cancellationToken);
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
              {{applicationName}} auth set
              {{applicationName}} auth status
              {{applicationName}} auth clear

            Commands:
              run          Stream a response from the configured model endpoint.
              auth set     Store the API credential for the current model endpoint.
              auth status  Show the credential status for the current model endpoint.
              auth clear   Remove the stored credential for the current model endpoint without changing the configured API key environment variable.
            """;

        await console.Out.WriteLineAsync(help.AsMemory(), cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
    }

    private async Task WriteAuthHelpAsync(CancellationToken cancellationToken)
    {
        var applicationName = options.Value.ApplicationName;
        var help = $$"""
            Usage:
              {{applicationName}} auth set
              {{applicationName}} auth status
              {{applicationName}} auth clear
            """;

        await console.Out.WriteLineAsync(help.AsMemory(), cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
    }
}
