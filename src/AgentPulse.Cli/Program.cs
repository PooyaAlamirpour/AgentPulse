using System.Text;
using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Console;
using AgentPulse.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ConfigureConsoleEncoding();
        var console = new SystemConsole();
        var errorRenderer = new CliErrorRenderer(
            console,
            NullLogger<CliErrorRenderer>.Instance);
        using var cancellationSource = new CancellationTokenSource();

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        global::System.Console.CancelKeyPress += cancelHandler;

        try
        {
            using var host = AgentPulseHost.CreateBuilder(console).Build();
            await host.StartAsync(cancellationSource.Token);

            try
            {
                var application = host.Services.GetRequiredService<CliApplication>();
                return await application.RunAsync(args, cancellationSource.Token);
            }
            finally
            {
                await AgentPulseHostShutdown.StopAsync(host, console);
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await errorRenderer.RenderCancellationAsync(cleanup.Token);
        }
        catch (Exception exception) when (
            exception is Microsoft.Extensions.Options.OptionsValidationException or
            InvalidOperationException)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await errorRenderer.RenderConfigurationAsync(exception, cleanup.Token);
        }
        catch (Exception exception)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await errorRenderer.RenderAsync(exception, cleanup.Token);
        }
        finally
        {
            global::System.Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void ConfigureConsoleEncoding()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        global::System.Console.InputEncoding = utf8;
        global::System.Console.OutputEncoding = utf8;
    }
}
