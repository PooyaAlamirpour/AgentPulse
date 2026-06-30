namespace AgentPulse.Infrastructure.Mutations;

internal enum FileMutationKind
{
    Add = 0,
    Update = 1,
    Delete = 2,
    Move = 3,
}

internal sealed record MutationPermissionTarget(
    string Operation,
    string RelativePath);

internal sealed record TextReplacement(
    string OldText,
    string NewText,
    bool ReplaceAll);

internal sealed record FileMutationOperation(
    FileMutationKind Kind,
    ResolvedMutationPath Source,
    ResolvedMutationPath? Destination,
    TextFileSnapshot? Before,
    string? AfterText,
    byte[]? AfterBytes,
    string AfterSha256,
    UnifiedDiffResult Diff,
    IReadOnlyList<MutationPermissionTarget> PermissionTargets);

internal sealed record FileMutationPlan(
    string WorkspaceRoot,
    string ToolOperation,
    IReadOnlyList<FileMutationOperation> Operations,
    UnifiedDiffResult Diff);

internal sealed record FileMutationResult(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Modified,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Moved,
    int Additions,
    int Deletions,
    string Diff,
    IReadOnlyDictionary<string, string> Sha256Before,
    IReadOnlyDictionary<string, string> Sha256After);
