namespace AgentPulse.Infrastructure.Mutations;

internal interface IUnifiedDiffGenerator
{
    UnifiedDiffResult CreateAdd(string relativePath, string newText);

    UnifiedDiffResult CreateUpdate(string relativePath, string oldText, string newText);

    UnifiedDiffResult CreateDelete(string relativePath, string oldText);

    UnifiedDiffResult CreateMove(
        string oldRelativePath,
        string newRelativePath,
        string oldText,
        string newText);

    UnifiedDiffResult Combine(IEnumerable<UnifiedDiffResult> diffs);
}

internal sealed record UnifiedDiffResult(
    string Text,
    string Preview,
    int Additions,
    int Deletions,
    bool WasPreviewTruncated);
