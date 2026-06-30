namespace AgentPulse.Application.Permissions;

public sealed class PermissionRuleEvaluator : IPermissionRuleEvaluator
{
    public PermissionEvaluationResult Evaluate(
        PermissionRequest request,
        IReadOnlyCollection<PermissionRule> rules,
        PermissionDecision defaultDecision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rules);

        var match = rules
            .Where(rule => Matches(rule, request))
            .Select(rule => new Candidate(rule, GetSpecificity(rule, request)))
            .OrderByDescending(static candidate => candidate.Specificity.Category)
            .ThenByDescending(static candidate => candidate.Specificity.OperationExact)
            .ThenByDescending(static candidate => candidate.Specificity.LiteralCharacters)
            .ThenBy(static candidate => candidate.Specificity.WildcardCount)
            .ThenByDescending(static candidate => SafetyRank(candidate.Rule.Decision))
            .ThenBy(static candidate => candidate.Rule.Tool, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Rule.Operation, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Rule.Target, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Rule.Scope)
            .ThenBy(static candidate => candidate.Rule.Source)
            .FirstOrDefault();

        if (match is null)
        {
            return new PermissionEvaluationResult(
                defaultDecision,
                null,
                request.ToolName,
                request.Operation,
                request.Target,
                "No permission rule matched; the default policy was used.",
                PermissionRuleSource.DefaultPolicy,
                null);
        }

        return new PermissionEvaluationResult(
            match.Rule.Decision,
            match.Rule,
            request.ToolName,
            request.Operation,
            request.Target,
            "The most specific matching permission rule was selected.",
            match.Rule.Source,
            match.Rule.Scope);
    }

    private static bool Matches(PermissionRule rule, PermissionRequest request)
    {
        return (rule.Tool == "*" || string.Equals(rule.Tool, request.ToolName, StringComparison.Ordinal)) &&
               (rule.Operation == "*" || string.Equals(rule.Operation, request.Operation, StringComparison.Ordinal)) &&
               PermissionPattern.IsMatch(rule.Target, request.Target);
    }

    private static Specificity GetSpecificity(PermissionRule rule, PermissionRequest request)
    {
        var exactTool = rule.Tool != "*";
        var wildcardTarget = rule.Target is "*" or "**";
        var exactTarget = !PermissionPattern.ContainsWildcard(rule.Target) &&
                          PermissionPattern.EqualsTarget(rule.Target, request.Target);
        var category = exactTool
            ? exactTarget
                ? 5
                : wildcardTarget
                    ? 3
                    : 4
            : wildcardTarget
                ? 1
                : 2;
        var literalCharacters = rule.Target.Count(static character => character != '*');
        var wildcardCount = rule.Target.Count(static character => character == '*');
        return new Specificity(
            category,
            rule.Operation == request.Operation ? 1 : 0,
            literalCharacters,
            wildcardCount);
    }

    private static int SafetyRank(PermissionDecision decision) => decision switch
    {
        PermissionDecision.Deny => 3,
        PermissionDecision.Ask => 2,
        PermissionDecision.Allow => 1,
        _ => 0,
    };

    private sealed record Candidate(PermissionRule Rule, Specificity Specificity);

    private readonly record struct Specificity(
        int Category,
        int OperationExact,
        int LiteralCharacters,
        int WildcardCount);
}
