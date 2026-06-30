namespace AgentPulse.Application.Permissions;

public sealed class PermissionAuthorizationContext
{
    private readonly Dictionary<string, PermissionDecision> _transientDecisions =
        new(StringComparer.Ordinal);

    internal bool TryGetDecision(
        PermissionRequest request,
        PermissionEvaluationResult evaluation,
        bool isResourceLevel,
        out PermissionDecision decision)
    {
        return _transientDecisions.TryGetValue(
            CreateKey(request, evaluation, isResourceLevel),
            out decision);
    }

    internal void RecordDecision(
        PermissionRequest request,
        PermissionEvaluationResult evaluation,
        bool isResourceLevel,
        PermissionDecision decision)
    {
        if (decision is not PermissionDecision.Allow and not PermissionDecision.Deny)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "Only transient allow or deny decisions can be recorded.");
        }

        _transientDecisions[CreateKey(request, evaluation, isResourceLevel)] = decision;
    }

    internal static string GetDecisionTarget(
        PermissionRequest request,
        PermissionEvaluationResult evaluation,
        bool isResourceLevel)
    {
        if (!isResourceLevel)
        {
            return request.Target;
        }

        var ruleTarget = evaluation.MatchedRule?.Target;
        return string.IsNullOrEmpty(ruleTarget) || ruleTarget == "*"
            ? request.Target
            : ruleTarget;
    }

    private static string CreateKey(
        PermissionRequest request,
        PermissionEvaluationResult evaluation,
        bool isResourceLevel)
    {
        var rule = evaluation.MatchedRule;
        var target = GetDecisionTarget(request, evaluation, isResourceLevel);
        if (OperatingSystem.IsWindows())
        {
            target = target.ToUpperInvariant();
        }

        return rule is null
            ? string.Join('\u001F', request.ToolName, request.Operation, target)
            : string.Join(
                '\u001F',
                rule.Tool,
                rule.Operation,
                target,
                rule.Decision,
                rule.Scope,
                rule.Source);
    }
}
