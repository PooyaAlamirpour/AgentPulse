using AgentPulse.Application.Permissions;
using AgentPulse.Cli.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Cli.IntegrationTests.Permissions;

public sealed class CliPermissionApprovalPromptTests
{
    [Theory]
    [InlineData("1\n", PermissionApprovalChoice.AllowOnce)]
    [InlineData("2\n", PermissionApprovalChoice.AllowSession)]
    [InlineData("3\n", PermissionApprovalChoice.AllowProject)]
    [InlineData("4\n", PermissionApprovalChoice.Deny)]
    public async Task Valid_choices_are_mapped(
        string input,
        PermissionApprovalChoice expected)
    {
        var console = new TestConsole(input);
        var prompt = new CliPermissionApprovalPrompt(
            console,
            NullLogger<CliPermissionApprovalPrompt>.Instance);

        var result = await prompt.RequestApprovalAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Contains("Permission required", console.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("Target: src/Program.cs", console.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_and_empty_input_retry_without_allowing()
    {
        var console = new TestConsole("\ninvalid\n4\n");
        var prompt = new CliPermissionApprovalPrompt(
            console,
            NullLogger<CliPermissionApprovalPrompt>.Instance);

        var result = await prompt.RequestApprovalAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(PermissionApprovalChoice.Deny, result);
        Assert.Equal(
            2,
            CountOccurrences(console.StandardError.ToString(), "Invalid choice."));
    }

    [Fact]
    public async Task Once_scope_shows_only_allow_once_and_deny()
    {
        var console = new TestConsole("2\n");
        var prompt = new CliPermissionApprovalPrompt(
            console,
            NullLogger<CliPermissionApprovalPrompt>.Instance);

        var result = await prompt.RequestApprovalAsync(
            CreateRequest(PermissionScope.Once),
            CancellationToken.None);
        var output = console.StandardError.ToString();

        Assert.Equal(PermissionApprovalChoice.Deny, result);
        Assert.Contains("[1] Allow once", output, StringComparison.Ordinal);
        Assert.Contains("[2] Deny", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Allow for this session", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Always allow for this project", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_scope_hides_project_approval()
    {
        var console = new TestConsole("3\n");
        var prompt = new CliPermissionApprovalPrompt(
            console,
            NullLogger<CliPermissionApprovalPrompt>.Instance);

        var result = await prompt.RequestApprovalAsync(
            CreateRequest(PermissionScope.Session),
            CancellationToken.None);
        var output = console.StandardError.ToString();

        Assert.Equal(PermissionApprovalChoice.Deny, result);
        Assert.Contains("[1] Allow once", output, StringComparison.Ordinal);
        Assert.Contains("[2] Allow for this session", output, StringComparison.Ordinal);
        Assert.Contains("[3] Deny", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Always allow for this project", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eof_returns_approval_unavailable()
    {
        var console = new TestConsole(string.Empty);
        var prompt = new CliPermissionApprovalPrompt(
            console,
            NullLogger<CliPermissionApprovalPrompt>.Instance);

        var result = await prompt.RequestApprovalAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal(PermissionApprovalChoice.Unavailable, result);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var console = new TestConsole(new BlockingReader(), false);
        var prompt = new CliPermissionApprovalPrompt(
            console,
            NullLogger<CliPermissionApprovalPrompt>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            prompt.RequestApprovalAsync(CreateRequest(), cancellation.Token));
    }

    private static PermissionRequest CreateRequest(
        PermissionScope maximumApprovalScope = PermissionScope.Project)
    {
        return new PermissionRequest(
            "read",
            "read",
            "src/Program.cs",
            Directory.GetCurrentDirectory(),
            SessionId.New(),
            ProjectId.New(),
            isInteractive: true,
            maximumApprovalScope: maximumApprovalScope);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private sealed class BlockingReader : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromCanceled<string?>(cancellationToken);
        }
    }
}
