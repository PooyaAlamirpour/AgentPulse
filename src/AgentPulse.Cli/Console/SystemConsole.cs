namespace AgentPulse.Cli.Console;

public sealed class SystemConsole : IConsole
{
    public TextReader In => global::System.Console.In;

    public TextWriter Out => global::System.Console.Out;

    public TextWriter Error => global::System.Console.Error;

    public bool IsInputRedirected => global::System.Console.IsInputRedirected;

    public bool IsOutputRedirected => global::System.Console.IsOutputRedirected;

    public bool IsErrorRedirected => global::System.Console.IsErrorRedirected;

    public bool IsInteractive =>
        !IsInputRedirected &&
        !IsOutputRedirected;
}
