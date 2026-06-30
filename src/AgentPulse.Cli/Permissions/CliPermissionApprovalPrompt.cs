using AgentPulse.Application.Permissions;
using AgentPulse.Cli.Console;
using Microsoft.Extensions.Logging;

namespace AgentPulse.Cli.Permissions;

public sealed class CliPermissionApprovalPrompt(
    IConsole console,
    ILogger<CliPermissionApprovalPrompt> logger) : IPermissionApprovalPrompt
{
    public bool IsInteractive => console.IsInteractive;

    public async Task<PermissionApprovalChoice> RequestApprovalAsync(
        PermissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var choices = CreateChoices(request.MaximumApprovalScope);

        while (true)
        {
            await WritePromptAsync(request, choices, cancellationToken);
            var input = await console.In.ReadLineAsync(cancellationToken);
            if (input is null)
            {
                logger.LogWarning("Permission approval input reached EOF and could not be resolved.");
                return PermissionApprovalChoice.Unavailable;
            }

            if (choices.TryGetValue(input.Trim(), out var choice))
            {
                return choice;
            }

            var validSelections = string.Join(", ", choices.Keys.OrderBy(static key => key, StringComparer.Ordinal));
            await console.Error.WriteLineAsync(
                $"Invalid choice. Enter {validSelections}.".AsMemory(),
                cancellationToken);
            await console.Error.FlushAsync(cancellationToken);
        }
    }

    private async Task WritePromptAsync(
        PermissionRequest request,
        IReadOnlyDictionary<string, PermissionApprovalChoice> choices,
        CancellationToken cancellationToken)
    {
        var options = string.Join(
            Environment.NewLine,
            choices.Select(static choice => $"  [{choice.Key}] {GetLabel(choice.Value)}"));
        var prompt = $"""

Permission required

Tool: {request.ToolName}
Operation: {request.Operation}
Target: {request.Target}

Choose an action:
{options}

Selection:
""";
        await console.Error.WriteAsync(prompt.AsMemory(), cancellationToken);
        await console.Error.FlushAsync(cancellationToken);
    }

    private static IReadOnlyDictionary<string, PermissionApprovalChoice> CreateChoices(
        PermissionScope maximumScope)
    {
        return maximumScope switch
        {
            PermissionScope.Once => new Dictionary<string, PermissionApprovalChoice>(StringComparer.Ordinal)
            {
                ["1"] = PermissionApprovalChoice.AllowOnce,
                ["2"] = PermissionApprovalChoice.Deny,
            },
            PermissionScope.Session => new Dictionary<string, PermissionApprovalChoice>(StringComparer.Ordinal)
            {
                ["1"] = PermissionApprovalChoice.AllowOnce,
                ["2"] = PermissionApprovalChoice.AllowSession,
                ["3"] = PermissionApprovalChoice.Deny,
            },
            PermissionScope.Project => new Dictionary<string, PermissionApprovalChoice>(StringComparer.Ordinal)
            {
                ["1"] = PermissionApprovalChoice.AllowOnce,
                ["2"] = PermissionApprovalChoice.AllowSession,
                ["3"] = PermissionApprovalChoice.AllowProject,
                ["4"] = PermissionApprovalChoice.Deny,
            },
            _ => throw new InvalidOperationException("The permission approval scope is invalid."),
        };
    }

    private static string GetLabel(PermissionApprovalChoice choice) => choice switch
    {
        PermissionApprovalChoice.AllowOnce => "Allow once",
        PermissionApprovalChoice.AllowSession => "Allow for this session",
        PermissionApprovalChoice.AllowProject => "Always allow for this project",
        PermissionApprovalChoice.Deny => "Deny",
        _ => throw new InvalidOperationException("The permission approval choice is invalid."),
    };
}
