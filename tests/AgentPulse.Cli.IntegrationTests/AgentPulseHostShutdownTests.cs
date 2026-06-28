using System.Diagnostics;
using AgentPulse.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class AgentPulseHostShutdownTests
{
    [Fact]
    public async Task Shutdown_timeout_is_bounded_and_writes_only_a_safe_stderr_diagnostic()
    {
        var console = new TestConsole();
        using var host = new NonStoppingHost();
        var stopwatch = Stopwatch.StartNew();

        await AgentPulseHostShutdown.StopAsync(
            host,
            console,
            TimeSpan.FromMilliseconds(75));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Shutdown exceeded its bounded test window: {stopwatch.Elapsed}.");
        Assert.Equal(string.Empty, console.StandardOutput.ToString());
        Assert.Contains(
            "Host shutdown exceeded",
            console.StandardError.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", console.StandardError.ToString(), StringComparison.Ordinal);
        Assert.True(host.StopToken.CanBeCanceled);
    }

    private sealed class NonStoppingHost : IHost
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly TaskCompletionSource _neverStops = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IServiceProvider Services => _services;

        public CancellationToken StopToken { get; private set; }

        public void Dispose()
        {
            _services.Dispose();
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopToken = cancellationToken;
            return _neverStops.Task;
        }
    }
}
