namespace AgentPulse.Application.Workspaces;

public interface IWorkspacePathResolver
{
    string Resolve(string workspaceRoot, string? requestedPath);
}
