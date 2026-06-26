using AgentPulse.Cli.Console;

namespace AgentPulse.Cli.IntegrationTests;

internal sealed class TestConsole : IConsole
{
    public TestConsole(string input = "", bool isInputRedirected = false)
        : this(new StringReader(input), isInputRedirected)
    {
    }

    public TestConsole(TextReader input, bool isInputRedirected)
    {
        In = input;
        IsInputRedirected = isInputRedirected;
    }

    public TextReader In { get; }

    public StringWriter StandardOutput { get; } = new();

    public StringWriter StandardError { get; } = new();

    public TextWriter Out => StandardOutput;

    public TextWriter Error => StandardError;

    public bool IsInputRedirected { get; }
}
