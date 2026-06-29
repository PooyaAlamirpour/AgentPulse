namespace AgentPulse.Application.AgentTools;

public sealed record AgentToolExecutionContext
{
    public AgentToolExecutionContext(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    public string WorkspaceRoot { get; }
}
