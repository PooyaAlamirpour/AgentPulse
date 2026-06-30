using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Permissions;
using AgentPulse.Infrastructure.Mutations;

namespace AgentPulse.Infrastructure.AgentTools;

internal sealed class WriteAgentTool(IWorkspaceMutationService mutationService)
    : IAgentTool,
      IPermissionAwareAgentTool,
      IAgentToolDefaultPermission,
      IDeferredPermissionAgentTool
{
    public IDeferredPermissionExecutionContract DeferredPermissionContract => mutationService;

    public string Name => "write";

    public string Description =>
        "Create a new text file or safely overwrite an existing file with its expected SHA-256. Prefer edit for small changes.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Workspace-relative file path."},"content":{"type":"string","description":"Complete text content."},"expected_sha256":{"type":"string","description":"Required when overwriting an existing file. Must be the SHA-256 hash of the file's exact current bytes."}},"required":["path","content"],"additionalProperties":false}
        """;

    public PermissionDecision DefaultPermissionDecision => PermissionDecision.Ask;

    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<WriteArguments>(arguments);
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The write tool requires a non-empty path.");
            }

            if (input.Content is null)
            {
                return MutationAgentToolResultFactory.DeterministicFailure(
                    "The write tool requires content. Empty content is allowed.");
            }

            var plan = await mutationService.PlanWriteAsync(
                context.WorkspaceRoot,
                input.Path,
                input.Content,
                input.ExpectedSha256,
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
                "The write operation failed because access to a workspace path was denied.");
        }
        catch (IOException)
        {
            return MutationAgentToolResultFactory.TransientFailure(
                "The write operation failed because of a file-system error.");
        }
    }

    public PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context)
    {
        try
        {
            var input = ToolArgumentReader.Deserialize<WriteArguments>(arguments);
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                return PermissionTargetResolution.Reject(
                    MutationAgentToolResultFactory.DeterministicFailure(
                        "The write tool requires a non-empty path."));
            }

            return PermissionTargetResolution.Success(
                "write",
                input.Path,
                $"Write workspace file '{input.Path}'.");
        }
        catch (ArgumentException exception)
        {
            return PermissionTargetResolution.Reject(
                MutationAgentToolResultFactory.DeterministicFailure(exception.Message));
        }
    }

    private sealed record WriteArguments(
        string? Path,
        string? Content,
        [property: JsonPropertyName("expected_sha256")] string? ExpectedSha256);
}
