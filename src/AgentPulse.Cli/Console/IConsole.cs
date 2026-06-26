namespace AgentPulse.Cli.Console;

public interface IConsole
{
    TextReader In { get; }

    TextWriter Out { get; }

    TextWriter Error { get; }

    bool IsInputRedirected { get; }
}
