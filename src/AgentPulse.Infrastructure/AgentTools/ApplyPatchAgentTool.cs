using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.Mutations;

namespace AgentPulse.Infrastructure.AgentTools;

internal sealed class ApplyPatchAgentTool(
    IApplyPatchParser patchParser,
    IWorkspaceMutationService mutationService)
    : IAgentTool,
      IPermissionAwareAgentTool,
      IAgentToolDefaultPermission,
      IDeferredPermissionAgentTool
{
    public IDeferredPermissionExecutionContract DeferredPermissionContract => mutationService;

    public string Name => "apply_patch";

    public string Description =>
        "Apply an atomic multi-file patch using the strict '*** Begin Patch' format. Any invalid or denied operation aborts the entire patch.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"patch_text":{"type":"string","description":"Patch text beginning with '*** Begin Patch' and ending with '*** End Patch'."}},"required":["patch_text"],"additionalProperties":false}
        """;

    public PermissionDecision DefaultPermissionDecision => PermissionDecision.Ask;

    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<ApplyPatchArguments>(arguments);
            if (input.PatchText is null)
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The apply_patch tool requires patch_text.");
            }

            var patch = patchParser.Parse(input.PatchText);
            var plan = await mutationService.PlanPatchAsync(
                context.WorkspaceRoot,
                patch,
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
            return MutationAgentToolResultFactory.DeterministicFailure(exception.Message);
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
                "The apply_patch operation failed because access to a workspace path was denied.");
        }
        catch (IOException)
        {
            return MutationAgentToolResultFactory.TransientFailure(
                "The apply_patch operation failed because of a file-system error.");
        }
    }

    public PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<ApplyPatchArguments>(arguments);
            if (string.IsNullOrWhiteSpace(input.PatchText))
            {
                return PermissionTargetResolution.Reject(
                    MutationAgentToolResultFactory.DeterministicFailure(
                        "The patch must not be empty."));
            }

            return PermissionTargetResolution.Success(
                "edit",
                ".",
                "Validate an atomic workspace patch before resource-level authorization.");
        }
        catch (ArgumentException exception)
        {
            return PermissionTargetResolution.Reject(
                MutationAgentToolResultFactory.DeterministicFailure(exception.Message));
        }
    }

    private sealed record ApplyPatchArguments(
        [property: JsonPropertyName("patch_text")] string? PatchText);
}
