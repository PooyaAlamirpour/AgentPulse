using AgentPulse.Application.Permissions;

namespace AgentPulse.Application.AgentTools;

public sealed record AgentToolExecutionContext
{
    private readonly IAgentToolResourcePermissionAuthorizer? _resourcePermissionAuthorizer;

    public AgentToolExecutionContext(
        string workspaceRoot,
        IAgentToolResourcePermissionAuthorizer? resourcePermissionAuthorizer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        _resourcePermissionAuthorizer = resourcePermissionAuthorizer;
    }

    public string WorkspaceRoot { get; }

    public bool HasResourcePermissionAuthorizer => _resourcePermissionAuthorizer is not null;

    public Task<PermissionAuthorizationResult> AuthorizeResourceAsync(
        string operation,
        string target,
        string? description,
        CancellationToken cancellationToken)
    {
        if (_resourcePermissionAuthorizer is null)
        {
            return Task.FromResult(PermissionAuthorizationResult.Reject(
                AgentToolResult.Failure(
                    $"Resource-level permission authorization is unavailable for '{target}'. The tool call cannot continue.")));
        }

        return _resourcePermissionAuthorizer.AuthorizeAsync(
            operation,
            target,
            description,
            cancellationToken);
    }
}
