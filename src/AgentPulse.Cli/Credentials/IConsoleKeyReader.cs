namespace AgentPulse.Cli.Credentials;

public interface IConsoleKeyReader
{
    bool KeyAvailable { get; }

    ConsoleKeyInfo ReadKey(bool intercept);
}
