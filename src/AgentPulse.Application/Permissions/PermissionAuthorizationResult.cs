using AgentPulse.Application.AgentTools;

namespace AgentPulse.Application.Permissions;

public sealed record PermissionAuthorizationResult
{
    private PermissionAuthorizationResult(
        PermissionAuthorizationStatus status,
        AgentToolResult? failure,
        PermissionEvaluationResult? resolution)
    {
        Status = status;
        Failure = failure;
        Resolution = resolution;
    }

    public PermissionAuthorizationStatus Status { get; }

    public bool IsAllowed => Status == PermissionAuthorizationStatus.Allowed;

    public bool IsExplicitlyDenied => Status == PermissionAuthorizationStatus.ExplicitlyDenied;

    public AgentToolResult? Failure { get; }

    public PermissionEvaluationResult? Resolution { get; }

    public static PermissionAuthorizationResult Allow(
        PermissionEvaluationResult? resolution = null) =>
        new(PermissionAuthorizationStatus.Allowed, null, resolution);

    public static PermissionAuthorizationResult Reject(
        AgentToolResult failure,
        PermissionEvaluationResult? resolution = null,
        PermissionAuthorizationStatus status = PermissionAuthorizationStatus.InfrastructureFailure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Succeeded)
        {
            throw new ArgumentException("A rejected permission must contain a failed tool result.", nameof(failure));
        }

        if (!Enum.IsDefined(status) || status == PermissionAuthorizationStatus.Allowed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "A rejected permission must have a failure status.");
        }

        if (status == PermissionAuthorizationStatus.ExplicitlyDenied &&
            resolution?.Decision != PermissionDecision.Deny)
        {
            throw new ArgumentException(
                "An explicit permission denial must contain a deny resolution.",
                nameof(resolution));
        }

        return new PermissionAuthorizationResult(status, failure, resolution);
    }
}
