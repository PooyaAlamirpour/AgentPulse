namespace AgentPulse.Cli.Console;

public interface IConsole
{
    TextReader In { get; }

    TextWriter Out { get; }

    TextWriter Error { get; }

    bool IsInputRedirected { get; }

    bool IsOutputRedirected { get; }

    bool IsErrorRedirected { get; }

    bool IsInteractive { get; }
}
