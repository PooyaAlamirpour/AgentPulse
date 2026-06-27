using AgentPulse.Domain.Sessions;

namespace AgentPulse.Cli.Commands;

public sealed class RunCommandParser : IRunCommandParser
{
    public ParsedRunCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? projectDirectory = null;
        string? modelOverride = null;
        SessionId? sessionId = null;
        var promptParts = new List<string>();
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (parseOptions && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                parseOptions = false;
                continue;
            }

            if (!parseOptions || !argument.StartsWith("-", StringComparison.Ordinal))
            {
                promptParts.Add(argument);
                continue;
            }

            switch (argument)
            {
                case "--dir":
                    projectDirectory = ReadUniqueValue(
                        arguments,
                        ref index,
                        argument,
                        projectDirectory is not null);
                    if (string.IsNullOrWhiteSpace(projectDirectory))
                    {
                        throw new RunCommandParsingException(
                            "The --dir value cannot be empty.");
                    }

                    break;
                case "--model":
                    modelOverride = ReadUniqueValue(
                        arguments,
                        ref index,
                        argument,
                        modelOverride is not null);
                    if (string.IsNullOrWhiteSpace(modelOverride))
                    {
                        throw new RunCommandParsingException(
                            "The --model value cannot be empty.");
                    }

                    modelOverride = modelOverride.Trim();
                    break;
                case "--session":
                    var sessionValue = ReadUniqueValue(
                        arguments,
                        ref index,
                        argument,
                        sessionId is not null);
                    if (!Guid.TryParse(sessionValue, out var parsedSessionId) ||
                        parsedSessionId == Guid.Empty)
                    {
                        throw new RunCommandParsingException(
                            "The --session value must be a valid non-empty session identifier.");
                    }

                    sessionId = new SessionId(parsedSessionId);
                    break;
                default:
                    throw new RunCommandParsingException(
                        $"Unknown run option: {argument}");
            }
        }

        var positionalPrompt = promptParts.Count == 0
            ? null
            : string.Join(" ", promptParts);

        return new ParsedRunCommand(
            positionalPrompt,
            projectDirectory,
            modelOverride,
            sessionId);
    }

    private static string ReadUniqueValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        bool alreadySpecified)
    {
        if (alreadySpecified)
        {
            throw new RunCommandParsingException(
                $"The {option} option cannot be specified more than once.");
        }

        if (index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new RunCommandParsingException(
                $"The {option} option requires a value.");
        }

        index++;
        return arguments[index];
    }
}
