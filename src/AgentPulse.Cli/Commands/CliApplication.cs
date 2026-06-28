using AgentPulse.Cli.Configuration;
using AgentPulse.Cli.Console;
using Microsoft.Extensions.Options;

namespace AgentPulse.Cli.Commands;

public sealed class CliApplication(
    IConsole console,
    IRunCommandParser runCommandParser,
    IPromptInputReader promptInputReader,
    IRunCommandHandler runCommandHandler,
    IAuthCommandHandler authCommandHandler,
    ICliErrorRenderer errorRenderer,
    IOptions<CliOptions> options)
{
    private const string EmptyPromptError =
        "A prompt is required as an argument or redirected standard input.";

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
            return await errorRenderer.RenderUsageAsync(
                $"Unknown command: {arguments[0]}",
                cancellationToken);
        }

        if (arguments.Count == 2 && IsHelp(arguments[1]))
        {
            await WriteRunHelpAsync(cancellationToken);
            return ExitCodes.Success;
        }

        ParsedRunCommand parsed;
        try
        {
            parsed = runCommandParser.Parse(arguments.Skip(1).ToArray());
        }
        catch (RunCommandParsingException exception)
        {
            return await errorRenderer.RenderUsageAsync(
                exception.Message,
                cancellationToken);
        }

        var prompt = await promptInputReader.ReadAsync(
            parsed.PositionalPrompt,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return await errorRenderer.RenderUsageAsync(
                EmptyPromptError,
                cancellationToken);
        }

        return await runCommandHandler.HandleAsync(
            new RunCommandOptions(
                prompt,
                parsed.ProjectDirectory,
                parsed.ModelOverride,
                parsed.SessionId),
            cancellationToken);
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
            return await errorRenderer.RenderUsageAsync(
                "Auth commands do not accept additional arguments.",
                cancellationToken);
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
              {{applicationName}} run [--dir <path>] [--model <model>] [--session <id>] [prompt]
              {{applicationName}} auth set
              {{applicationName}} auth status
              {{applicationName}} auth clear

            Commands:
              run          Stream a response from the configured model endpoint.
              auth set     Store the API credential for the current model endpoint.
              auth status  Show the credential status for the current model endpoint.
              auth clear   Remove the stored credential for the current model endpoint without changing the configured API key environment variable.

            Run options:
              --dir <path>       Project directory. Defaults to the current directory.
              --model <model>    Model override for this run only.
              --session <id>     Continue an existing session in the resolved project.
              stdin              Used when no prompt argument is supplied.

            Output:
              stdout             Streamed model response only.
              stderr             Session ID after success, plus metadata and errors.

            Behavior:
              Redirected stdin is read only when no positional prompt is supplied.
              Ctrl+C preserves any partial response and exits with code 130.
              Only one active run is allowed per session.
              Non-interactive runs never open a credential prompt.

            Examples:
              {{applicationName}} run "Explain this project"
              {{applicationName}} run --dir <path> "Explain this project"
              {{applicationName}} run --model <model> "Explain this project"
              {{applicationName}} run --session <id> "Continue"
              echo "Explain this project" | {{applicationName}} run

            Git repositories:
              Repository subdirectories resolve to the same canonical project root.
            """;

        await console.Out.WriteLineAsync(help.AsMemory(), cancellationToken);
        await console.Out.FlushAsync(cancellationToken);
    }

    private async Task WriteRunHelpAsync(CancellationToken cancellationToken)
    {
        var applicationName = options.Value.ApplicationName;
        var help = $$"""
            Usage:
              {{applicationName}} run [--dir <path>] [--model <model>] [--session <id>] [prompt]

            Options:
              --dir <path>       Project directory. Defaults to the current directory.
              --model <model>    Model override for this run only.
              --session <id>     Continue an existing session in the resolved project.
              stdin              Used when no prompt argument is supplied.

            Output:
              stdout             Streamed model response only.
              stderr             Session ID after success, plus metadata and errors.

            Behavior:
              Redirected stdin is read only when no positional prompt is supplied.
              Ctrl+C preserves any partial response and exits with code 130.
              Only one active run is allowed per session.
              Non-interactive runs never open a credential prompt.

            Examples:
              {{applicationName}} run "Explain this project"
              {{applicationName}} run --dir <path> "Explain this project"
              {{applicationName}} run --model <model> "Explain this project"
              {{applicationName}} run --session <id> "Continue"
              echo "Explain this project" | {{applicationName}} run

            Git repositories:
              Repository subdirectories resolve to the same canonical project root.
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
