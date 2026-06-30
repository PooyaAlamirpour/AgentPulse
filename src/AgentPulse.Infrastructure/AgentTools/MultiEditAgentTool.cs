using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.Mutations;

namespace AgentPulse.Infrastructure.AgentTools;

internal sealed class MultiEditAgentTool(IWorkspaceMutationService mutationService)
    : IAgentTool,
      IPermissionAwareAgentTool,
      IAgentToolDefaultPermission,
      IDeferredPermissionAgentTool
{
    public IDeferredPermissionExecutionContract DeferredPermissionContract => mutationService;

    public string Name => "multi_edit";

    public string Description =>
        "Apply multiple exact replacements atomically to one file. If any edit fails, no changes are written.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Workspace-relative file path."},"edits":{"type":"array","minItems":1,"items":{"type":"object","properties":{"old_text":{"type":"string","description":"Exact staged text to replace."},"new_text":{"type":"string","description":"Replacement text."},"replace_all":{"type":"boolean","description":"Replace every exact occurrence. Defaults to false."}},"required":["old_text","new_text"],"additionalProperties":false}}},"required":["path","edits"],"additionalProperties":false}
        """;

    public PermissionDecision DefaultPermissionDecision => PermissionDecision.Ask;

    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<MultiEditArguments>(arguments);
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The multi_edit tool requires a non-empty path.");
            }

            if (input.Edits is null || input.Edits.Count == 0)
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The multi_edit tool requires at least one edit.");
            }

            for (var index = 0; index < input.Edits.Count; index++)
            {
                if (input.Edits[index].OldText is null)
                {
                    return MutationAgentToolResultFactory.DeterministicFailure(
                        $"Multi-edit operation {index + 1} requires old_text.");
                }

                if (input.Edits[index].NewText is null)
                {
                    return MutationAgentToolResultFactory.DeterministicFailure(
                        $"Multi-edit operation {index + 1} requires new_text. Empty replacement text is allowed.");
                }
            }

            var edits = input.Edits.Select(static edit => new TextReplacement(
                edit.OldText!,
                edit.NewText!,
                edit.ReplaceAll ?? false)).ToArray();
            var plan = await mutationService.PlanEditAsync(
                context.WorkspaceRoot,
                input.Path,
                edits,
                multiEdit: true,
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
                "The multi_edit operation failed because access to a workspace path was denied.");
        }
        catch (IOException)
        {
            return MutationAgentToolResultFactory.TransientFailure(
                "The multi_edit operation failed because of a file-system error.");
        }
    }

    public PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<MultiEditArguments>(arguments);
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                return PermissionTargetResolution.Reject(
                    MutationAgentToolResultFactory.DeterministicFailure(
                        "The multi_edit tool requires a non-empty path."));
            }

            return PermissionTargetResolution.Success(
                "edit",
                input.Path,
                $"Atomically edit workspace file '{input.Path}'.");
        }
        catch (ArgumentException exception)
        {
            return PermissionTargetResolution.Reject(
                MutationAgentToolResultFactory.DeterministicFailure(exception.Message));
        }
    }

    private sealed record MultiEditArguments(string? Path, IReadOnlyList<MultiEditEntry>? Edits);

    private sealed record MultiEditEntry(
        [property: JsonPropertyName("old_text")] string? OldText,
        [property: JsonPropertyName("new_text")] string? NewText,
        [property: JsonPropertyName("replace_all")] bool? ReplaceAll);
}
