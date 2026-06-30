namespace AgentPulse.Application.Permissions;

public enum PermissionApprovalChoice
{
    AllowOnce = 0,
    AllowSession = 1,
    AllowProject = 2,
    Deny = 3,
    Unavailable = 4,
}
