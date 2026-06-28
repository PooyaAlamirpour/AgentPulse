using AgentPulse.Application.ChatModels;
using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.ProjectContexts;
using AgentPulse.Cli.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Cli.IntegrationTests;

public sealed class CliErrorRendererTests
{
    [Fact]
    public async Task Wrapped_provider_timeout_uses_the_timeout_exit_code()
    {
        var console = new TestConsole();
        var renderer = CreateRenderer(console);
        var providerException = new ModelProviderException(
            ModelProviderErrorCode.Timeout,
            "sensitive provider timeout detail");
        var exception = new ModelRunException(
            ModelRunErrorCode.ProviderFailure,
            "provider failure",
            providerException);

        var exitCode = await renderer.RenderAsync(exception, CancellationToken.None);

        Assert.Equal(ExitCodes.Timeout, exitCode);
        Assert.Contains("timed out", console.StandardError.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "sensitive provider timeout detail",
            console.StandardError.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_cancellation_uses_the_cancellation_exit_code()
    {
        var console = new TestConsole();
        var renderer = CreateRenderer(console);

        var exitCode = await renderer.RenderAsync(
            new OperationCanceledException(),
            CancellationToken.None);

        Assert.Equal(ExitCodes.Cancelled, exitCode);
        Assert.Contains("cancelled", console.StandardError.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_project_path_uses_the_usage_exit_code_without_rendering_the_path()
    {
        var console = new TestConsole();
        var renderer = CreateRenderer(console);
        const string privatePath = "/home/example/private/project";

        var exitCode = await renderer.RenderAsync(
            new ProjectContextException(
                ProjectContextErrorCode.PathNotFound,
                $"Project path '{privatePath}' was not found."),
            CancellationToken.None);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.DoesNotContain(privatePath, console.StandardError.ToString(), StringComparison.Ordinal);
    }

    private static CliErrorRenderer CreateRenderer(TestConsole console)
    {
        return new CliErrorRenderer(
            console,
            NullLogger<CliErrorRenderer>.Instance);
    }
}
