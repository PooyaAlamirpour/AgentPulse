namespace AgentPulse.Application.Permissions;

public interface IPermissionRuleEvaluator
{
    PermissionEvaluationResult Evaluate(
        PermissionRequest request,
        IReadOnlyCollection<PermissionRule> rules,
        PermissionDecision defaultDecision);
}
