namespace AgentPulse.Application.ProjectContexts;

public sealed record GitRepositoryInfo(
    string WorkingTreeRoot,
    string GitDirectory,
    string CommonGitDirectory,
    bool IsLinkedWorktree);
