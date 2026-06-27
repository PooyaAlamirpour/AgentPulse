namespace AgentPulse.Cli.Credentials;

public sealed class SystemConsoleKeyReader : IConsoleKeyReader
{
    public bool KeyAvailable => global::System.Console.KeyAvailable;

    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        return global::System.Console.ReadKey(intercept);
    }
}
