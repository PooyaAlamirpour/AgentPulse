namespace AgentPulse.Application.AgentTools;

public sealed record PermissionTargetResolution
{
    private PermissionTargetResolution(
        bool succeeded,
        string? operation,
        string? target,
        string? description,
        AgentToolResult? failure)
    {
        Succeeded = succeeded;
        Operation = operation;
        Target = target;
        Description = description;
        Failure = failure;
    }

    public bool Succeeded { get; }

    public string? Operation { get; }

    public string? Target { get; }

    public string? Description { get; }

    public AgentToolResult? Failure { get; }

    public static PermissionTargetResolution Success(
        string operation,
        string target,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return new PermissionTargetResolution(true, operation.Trim(), target.Trim(), description, null);
    }

    public static PermissionTargetResolution Reject(AgentToolResult failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Succeeded)
        {
            throw new ArgumentException("A rejected target resolution must contain a failed tool result.", nameof(failure));
        }

        return new PermissionTargetResolution(false, null, null, null, failure);
    }
}
