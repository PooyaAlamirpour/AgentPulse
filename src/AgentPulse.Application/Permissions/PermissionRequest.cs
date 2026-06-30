using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Permissions;

public sealed record PermissionRequest
{
    public PermissionRequest(
        string toolName,
        string operation,
        string target,
        string workspaceRoot,
        SessionId sessionId,
        ProjectId projectId,
        bool isInteractive,
        string? description = null,
        PermissionScope maximumApprovalScope = PermissionScope.Project)
    {
        if (!Enum.IsDefined(maximumApprovalScope))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumApprovalScope),
                maximumApprovalScope,
                "Permission approval scope is invalid.");
        }

        ToolName = PermissionPattern.NormalizeSelector(toolName, nameof(toolName));
        Operation = PermissionPattern.NormalizeSelector(operation, nameof(operation));
        Target = PermissionPattern.NormalizeRequestTarget(target, nameof(target));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        SessionId = sessionId;
        ProjectId = projectId;
        IsInteractive = isInteractive;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        MaximumApprovalScope = maximumApprovalScope;
    }

    public string ToolName { get; }

    public string Operation { get; }

    public string Target { get; }

    public string WorkspaceRoot { get; }

    public SessionId SessionId { get; }

    public ProjectId ProjectId { get; }

    public bool IsInteractive { get; }

    public string? Description { get; }

    public PermissionScope MaximumApprovalScope { get; }

    public PermissionRequest WithMaximumApprovalScope(PermissionScope scope)
    {
        return new PermissionRequest(
            ToolName,
            Operation,
            Target,
            WorkspaceRoot,
            SessionId,
            ProjectId,
            IsInteractive,
            Description,
            scope);
    }

    internal PermissionRequest WithTarget(string target)
    {
        return new PermissionRequest(
            ToolName,
            Operation,
            target,
            WorkspaceRoot,
            SessionId,
            ProjectId,
            IsInteractive,
            Description,
            MaximumApprovalScope);
    }
}
