using System.Text;
using AgentPulse.Cli.Console;

namespace AgentPulse.Cli.Credentials;

public sealed class SystemSecretInputReader(
    IConsole console,
    IConsoleKeyReader keyReader) : ISecretInputReader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        var value = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (!keyReader.KeyAvailable)
            {
                await Task.Delay(PollInterval, cancellationToken);
            }

            var key = keyReader.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.C &&
                key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                throw new SecretInputCancelledException();
            }

            if (key.Key == ConsoleKey.Enter)
            {
                await console.Error.WriteLineAsync(
                    ReadOnlyMemory<char>.Empty,
                    cancellationToken);
                await console.Error.FlushAsync(cancellationToken);
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }
}
