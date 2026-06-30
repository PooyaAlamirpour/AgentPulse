using AgentPulse.Application.AgentTools;

namespace AgentPulse.Infrastructure.Mutations;

internal interface IWorkspaceMutationService : IDeferredPermissionExecutionContract
{
    Task<FileMutationPlan> PlanWriteAsync(
        string workspaceRoot,
        string path,
        string content,
        string? expectedSha256,
        CancellationToken cancellationToken);

    Task<FileMutationPlan> PlanEditAsync(
        string workspaceRoot,
        string path,
        IReadOnlyList<TextReplacement> edits,
        bool multiEdit,
        CancellationToken cancellationToken);

    Task<FileMutationPlan> PlanPatchAsync(
        string workspaceRoot,
        PatchDocument patch,
        CancellationToken cancellationToken);

    Task<FileMutationResult> AuthorizeAndCommitAsync(
        FileMutationPlan plan,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken);
}
