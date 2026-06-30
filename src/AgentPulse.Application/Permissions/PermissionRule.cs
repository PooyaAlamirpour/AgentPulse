namespace AgentPulse.Application.Permissions;

public sealed record PermissionRule
{
    private PermissionRule(
        string tool,
        string operation,
        string target,
        PermissionDecision decision,
        PermissionScope scope,
        PermissionRuleSource source)
    {
        Tool = tool;
        Operation = operation;
        Target = target;
        Decision = decision;
        Scope = scope;
        Source = source;
    }

    public string Tool { get; }

    public string Operation { get; }

    public string Target { get; }

    public PermissionDecision Decision { get; }

    public PermissionScope Scope { get; }

    public PermissionRuleSource Source { get; }

    public static PermissionRule Create(
        string tool,
        string operation,
        string target,
        PermissionDecision decision,
        PermissionScope scope = PermissionScope.Project,
        PermissionRuleSource source = PermissionRuleSource.Configuration)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "Permission decision is invalid.");
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Permission scope is invalid.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Permission rule source is invalid.");
        }

        return new PermissionRule(
            PermissionPattern.NormalizeSelector(tool, nameof(tool)),
            PermissionPattern.NormalizeSelector(operation, nameof(operation)),
            PermissionPattern.NormalizeTargetPattern(target, nameof(target)),
            decision,
            scope,
            source);
    }
}
