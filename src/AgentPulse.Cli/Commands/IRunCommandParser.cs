namespace AgentPulse.Cli.Commands;

public interface IRunCommandParser
{
    ParsedRunCommand Parse(IReadOnlyList<string> arguments);
}
