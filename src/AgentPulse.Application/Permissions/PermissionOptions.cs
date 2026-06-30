namespace AgentPulse.Application.Permissions;

public sealed class PermissionOptions
{
    public const string SectionName = "AgentPulse:Permissions";

    public string DefaultDecision { get; set; } = nameof(PermissionDecision.Allow);

    public List<PermissionRuleOptions> Rules { get; set; } = [];

    public PermissionDecision GetDefaultDecision()
    {
        if (!TryParseNamedEnum(DefaultDecision, out PermissionDecision decision))
        {
            throw new InvalidOperationException(
                $"{SectionName}:DefaultDecision must be Allow, Ask, or Deny.");
        }

        return decision;
    }

    public IReadOnlyList<PermissionRule> CreateRules()
    {
        _ = GetDefaultDecision();
        if (Rules is null)
        {
            throw new InvalidOperationException($"{SectionName}:Rules must be an array.");
        }

        var rules = new List<PermissionRule>(Rules.Count);
        var identities = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        for (var index = 0; index < Rules.Count; index++)
        {
            var configured = Rules[index] ?? throw new InvalidOperationException(
                $"{SectionName}:Rules:{index} must not be null.");
            var decision = ParseDecision(configured.Decision, index);
            var scope = ParseScope(configured.Scope, index);
            PermissionRule rule;
            try
            {
                rule = PermissionRule.Create(
                    configured.Tool,
                    configured.Operation,
                    configured.Target,
                    decision,
                    scope,
                    PermissionRuleSource.Configuration);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:Rules:{index} is invalid: {exception.Message}",
                    exception);
            }

            var identity = string.Join('\u001F', rule.Tool, rule.Operation, rule.Target);
            if (!identities.Add(identity))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:Rules contains a duplicate rule for tool '{rule.Tool}', operation '{rule.Operation}', and target '{rule.Target}'.");
            }

            rules.Add(rule);
        }

        return rules;
    }

    public void Validate()
    {
        _ = CreateRules();
    }

    private static PermissionDecision ParseDecision(string value, int index)
    {
        if (!TryParseNamedEnum(value, out PermissionDecision decision))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Rules:{index}:Decision must be Allow, Ask, or Deny.");
        }

        return decision;
    }

    private static PermissionScope ParseScope(string value, int index)
    {
        if (!TryParseNamedEnum(value, out PermissionScope scope))
        {
            throw new InvalidOperationException(
                $"{SectionName}:Rules:{index}:Scope must be Once, Session, or Project.");
        }

        return scope;
    }

    private static bool TryParseNamedEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return Enum.GetNames<TEnum>().Contains(normalized, StringComparer.OrdinalIgnoreCase) &&
               Enum.TryParse(normalized, ignoreCase: true, out result);
    }
}

public sealed class PermissionRuleOptions
{
    public string Tool { get; set; } = string.Empty;

    public string Operation { get; set; } = "*";

    public string Target { get; set; } = string.Empty;

    public string Decision { get; set; } = string.Empty;

    public string Scope { get; set; } = nameof(PermissionScope.Project);
}
