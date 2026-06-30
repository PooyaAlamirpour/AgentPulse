namespace AgentPulse.Application.Permissions;

public enum PermissionRuleSource
{
    DefaultPolicy = 0,
    Configuration = 1,
    OneTimeApproval = 2,
    SessionApproval = 3,
    ProjectApproval = 4,
    NonInteractivePolicy = 5,
    UserDecision = 6,
}
