namespace AgentPulse.Application.Permissions;

public enum PermissionAuthorizationStatus
{
    Allowed = 0,
    ExplicitlyDenied = 1,
    ApprovalUnavailable = 2,
    InvalidApproval = 3,
    InfrastructureFailure = 4,
}
