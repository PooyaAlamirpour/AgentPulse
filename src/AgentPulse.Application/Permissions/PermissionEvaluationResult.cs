namespace AgentPulse.Application.Permissions;

public sealed record PermissionEvaluationResult(
    PermissionDecision Decision,
    PermissionRule? MatchedRule,
    string ToolName,
    string Operation,
    string Target,
    string Reason,
    PermissionRuleSource RuleSource,
    PermissionScope? PersistenceScope);
