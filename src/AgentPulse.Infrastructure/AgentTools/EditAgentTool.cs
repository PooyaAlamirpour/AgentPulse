using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.Mutations;

namespace AgentPulse.Infrastructure.AgentTools;

internal sealed class EditAgentTool(IWorkspaceMutationService mutationService)
    : IAgentTool,
      IPermissionAwareAgentTool,
      IAgentToolDefaultPermission,
      IDeferredPermissionAgentTool
{
    public IDeferredPermissionExecutionContract DeferredPermissionContract => mutationService;

    public string Name => "edit";

    public string Description =>
        "Replace one exact, unambiguous text occurrence in an existing file. Set replace_all to replace every exact occurrence.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Workspace-relative file path."},"old_text":{"type":"string","description":"Exact existing text to replace."},"new_text":{"type":"string","description":"Replacement text."},"replace_all":{"type":"boolean","description":"Replace every exact occurrence. Defaults to false."}},"required":["path","old_text","new_text"],"additionalProperties":false}
        """;

    public PermissionDecision DefaultPermissionDecision => PermissionDecision.Ask;

    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<EditArguments>(arguments);
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The edit tool requires a non-empty path.");
            }

            if (input.OldText is null)
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The edit tool requires old_text.");
            }

            if (input.NewText is null)
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The edit tool requires new_text. Empty replacement text is allowed.");
            }

            var plan = await mutationService.PlanEditAsync(
                context.WorkspaceRoot,
                input.Path,
                [new TextReplacement(input.OldText, input.NewText, input.ReplaceAll ?? false)],
                multiEdit: false,
                cancellationToken);
            var result = await mutationService.AuthorizeAndCommitAsync(
                plan,
                context,
                cancellationToken);
            return MutationAgentToolResultFactory.Success(Name, result);
        }
        catch (MutationPermissionException exception)
        {
            return exception.Failure;
        }
        catch (MutationValidationException exception)
        {
            return MutationAgentToolResultFactory.DeterministicFailure(
                char.ToUpperInvariant(exception.Message[0]) + exception.Message[1..]);
        }
        catch (ArgumentException exception)
        {
            return MutationAgentToolResultFactory.DeterministicFailure(exception.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MutationRollbackException exception)
        {
            return MutationAgentToolResultFactory.TransientFailure(exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return MutationAgentToolResultFactory.TransientFailure(
                "The edit operation failed because access to a workspace path was denied.");
        }
        catch (IOException)
        {
            return MutationAgentToolResultFactory.TransientFailure(
                "The edit operation failed because of a file-system error.");
        }
    }

    public PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<EditArguments>(arguments);
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                return PermissionTargetResolution.Reject(
                    MutationAgentToolResultFactory.DeterministicFailure(
                        "The edit tool requires a non-empty path."));
            }

            return PermissionTargetResolution.Success(
                "edit",
                input.Path,
                $"Edit workspace file '{input.Path}'.");
        }
        catch (ArgumentException exception)
        {
            return PermissionTargetResolution.Reject(
                MutationAgentToolResultFactory.DeterministicFailure(exception.Message));
        }
    }

    private sealed record EditArguments(
        string? Path,
        [property: JsonPropertyName("old_text")] string? OldText,
        [property: JsonPropertyName("new_text")] string? NewText,
        [property: JsonPropertyName("replace_all")] bool? ReplaceAll);
}
