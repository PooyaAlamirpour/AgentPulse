namespace AgentPulse.Infrastructure.Mutations;

internal interface IProtectedPathPolicy
{
    ResolvedMutationPath ResolveAndValidate(string workspaceRoot, string requestedPath);
}

internal sealed record ResolvedMutationPath(string FullPath, string RelativePath);
