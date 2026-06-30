namespace AgentPulse.Application.Permissions;

public sealed record PermissionApproval
{
    private PermissionApproval(string toolName, string operation, string target)
    {
        ToolName = toolName;
        Operation = operation;
        Target = target;
    }

    public string ToolName { get; }

    public string Operation { get; }

    public string Target { get; }

    public static PermissionApproval FromRequest(PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PermissionApproval(request.ToolName, request.Operation, request.Target);
    }

    public static PermissionApproval Create(string toolName, string operation, string target)
    {
        return new PermissionApproval(
            PermissionPattern.NormalizeSelector(toolName, nameof(toolName)),
            PermissionPattern.NormalizeSelector(operation, nameof(operation)),
            PermissionPattern.NormalizeRequestTarget(target, nameof(target)));
    }
}
