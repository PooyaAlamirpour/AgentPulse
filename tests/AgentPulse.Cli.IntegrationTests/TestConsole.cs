using AgentPulse.Cli.Console;

namespace AgentPulse.Cli.IntegrationTests;

internal sealed class TestConsole : IConsole
{
    public TestConsole(
        string input = "",
        bool isInputRedirected = false,
        bool isOutputRedirected = false,
        bool isErrorRedirected = false)
        : this(
            new StringReader(input),
            isInputRedirected,
            isOutputRedirected,
            isErrorRedirected)
    {
    }

    public TestConsole(
        TextReader input,
        bool isInputRedirected,
        bool isOutputRedirected = false,
        bool isErrorRedirected = false)
    {
        In = input;
        IsInputRedirected = isInputRedirected;
        IsOutputRedirected = isOutputRedirected;
        IsErrorRedirected = isErrorRedirected;
    }

    public TextReader In { get; }

    public StringWriter StandardOutput { get; } = new();

    public StringWriter StandardError { get; } = new();

    public TextWriter Out => StandardOutput;

    public TextWriter Error => StandardError;

    public bool IsInputRedirected { get; }

    public bool IsOutputRedirected { get; }

    public bool IsErrorRedirected { get; }

    public bool IsInteractive =>
        !IsInputRedirected &&
        !IsOutputRedirected;
}
