using AgentPulse.Cli.Commands;
using AgentPulse.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPulse.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellationSource = new CancellationTokenSource();

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        global::System.Console.CancelKeyPress += cancelHandler;

        try
        {
            using var host = AgentPulseHost.CreateBuilder().Build();
            await host.StartAsync(cancellationSource.Token);

            try
            {
                var application = host.Services.GetRequiredService<CliApplication>();
                return await application.RunAsync(args, cancellationSource.Token);
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            await global::System.Console.Error.WriteLineAsync("Operation cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            await global::System.Console.Error.WriteLineAsync($"Fatal error: {exception.Message}");
            return ExitCodes.Failure;
        }
        finally
        {
            global::System.Console.CancelKeyPress -= cancelHandler;
        }
    }
}
