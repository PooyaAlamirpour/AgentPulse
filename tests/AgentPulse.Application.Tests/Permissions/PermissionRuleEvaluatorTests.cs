using AgentPulse.Application.Permissions;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.Tests.Permissions;

public sealed class PermissionRuleEvaluatorTests
{
    private readonly PermissionRuleEvaluator _evaluator = new();

    [Fact]
    public void Exact_rule_beats_wildcard_rule()
    {
        var result = Evaluate(
            PermissionRule.Create("*", "*", "*", PermissionDecision.Deny),
            PermissionRule.Create("read", "read", "src/Program.cs", PermissionDecision.Allow));

        Assert.Equal(PermissionDecision.Allow, result.Decision);
        Assert.Equal("src/Program.cs", result.MatchedRule?.Target);
    }

    [Fact]
    public void Specific_path_beats_general_path()
    {
        var result = Evaluate(
            PermissionRule.Create("read", "read", "src/**", PermissionDecision.Ask),
            PermissionRule.Create("read", "read", "src/Generated/**", PermissionDecision.Deny),
            target: "src/Generated/File.cs");

        Assert.Equal(PermissionDecision.Deny, result.Decision);
        Assert.Equal("src/Generated/**", result.MatchedRule?.Target);
    }

    [Theory]
    [InlineData(PermissionDecision.Deny, PermissionDecision.Ask, PermissionDecision.Deny)]
    [InlineData(PermissionDecision.Ask, PermissionDecision.Allow, PermissionDecision.Ask)]
    public void Safer_decision_wins_at_equal_specificity(
        PermissionDecision first,
        PermissionDecision second,
        PermissionDecision expected)
    {
        var result = Evaluate(
            PermissionRule.Create("read", "read", "src/*", first),
            PermissionRule.Create("read", "read", "src/*", second));

        Assert.Equal(expected, result.Decision);
    }

    [Fact]
    public void Equivalent_global_wildcards_use_the_safer_decision()
    {
        var result = Evaluate(
            PermissionRule.Create("read", "read", "*", PermissionDecision.Allow),
            PermissionRule.Create("read", "read", "**", PermissionDecision.Deny));

        Assert.Equal(PermissionDecision.Deny, result.Decision);
    }

    [Fact]
    public void Default_policy_is_used_when_no_rule_matches()
    {
        var request = CreateRequest("read", "read", "src/Program.cs");

        var result = _evaluator.Evaluate(request, [], PermissionDecision.Allow);

        Assert.Equal(PermissionDecision.Allow, result.Decision);
        Assert.Null(result.MatchedRule);
        Assert.Equal(PermissionRuleSource.DefaultPolicy, result.RuleSource);
    }

    [Fact]
    public void Windows_separators_are_normalized()
    {
        var rule = PermissionRule.Create("read", "read", "src\\**", PermissionDecision.Deny);
        var request = CreateRequest("read", "read", "src\\Program.cs");

        var result = _evaluator.Evaluate(request, [rule], PermissionDecision.Allow);

        Assert.Equal(PermissionDecision.Deny, result.Decision);
        Assert.Equal("src/**", result.MatchedRule?.Target);
        Assert.Equal("src/Program.cs", result.Target);
    }

    [Fact]
    public void Unix_separators_are_preserved()
    {
        var result = Evaluate(
            PermissionRule.Create("read", "read", "tests/**", PermissionDecision.Ask),
            target: "tests/PermissionTests.cs");

        Assert.Equal(PermissionDecision.Ask, result.Decision);
    }

    [Fact]
    public void Case_sensitivity_follows_platform_behavior()
    {
        var result = Evaluate(
            PermissionRule.Create("read", "read", "src/program.cs", PermissionDecision.Deny),
            target: "src/Program.cs");

        Assert.Equal(
            OperatingSystem.IsWindows() ? PermissionDecision.Deny : PermissionDecision.Allow,
            result.Decision);
    }

    [Fact]
    public void Traversal_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CreateRequest("read", "read", "../secret.txt"));
        Assert.Throws<ArgumentException>(() =>
            PermissionRule.Create("read", "read", "src/../secret.txt", PermissionDecision.Deny));
    }

    [Fact]
    public void Invalid_wildcard_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PermissionRule.Create("read", "read", "src/[a].cs", PermissionDecision.Deny));
        Assert.Throws<ArgumentException>(() =>
            PermissionRule.Create("read", "read", "src/***.cs", PermissionDecision.Deny));
    }

    [Fact]
    public void Exact_root_target_does_not_match_a_nested_file_with_the_same_name()
    {
        var result = Evaluate(
            PermissionRule.Create("read", "read", "secrets.txt", PermissionDecision.Deny),
            target: "src/secrets.txt");

        Assert.Equal(PermissionDecision.Allow, result.Decision);
    }

    [Fact]
    public void Double_star_must_be_a_complete_path_segment()
    {
        Assert.Throws<ArgumentException>(() =>
            PermissionRule.Create("read", "read", "src/a**b.cs", PermissionDecision.Deny));
    }

    [Fact]
    public void Empty_tool_and_target_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PermissionRule.Create("", "read", "src/**", PermissionDecision.Allow));
        Assert.Throws<ArgumentException>(() =>
            PermissionRule.Create("read", "read", "", PermissionDecision.Allow));
    }

    [Fact]
    public void Result_is_independent_of_collection_order()
    {
        var rules = new[]
        {
            PermissionRule.Create("*", "*", "*", PermissionDecision.Allow),
            PermissionRule.Create("read", "read", "src/**", PermissionDecision.Ask),
            PermissionRule.Create("read", "read", "src/Program.cs", PermissionDecision.Deny),
        };

        var forward = _evaluator.Evaluate(
            CreateRequest("read", "read", "src/Program.cs"),
            rules,
            PermissionDecision.Allow);
        var reverse = _evaluator.Evaluate(
            CreateRequest("read", "read", "src/Program.cs"),
            rules.Reverse().ToArray(),
            PermissionDecision.Allow);

        Assert.Equal(forward.Decision, reverse.Decision);
        Assert.Equal(forward.MatchedRule, reverse.MatchedRule);
    }

    [Fact]
    public void Equivalent_rules_are_deterministic_when_metadata_differs()
    {
        var first = PermissionRule.Create(
            "read",
            "read",
            "src/**",
            PermissionDecision.Ask,
            PermissionScope.Session,
            PermissionRuleSource.Configuration);
        var second = PermissionRule.Create(
            "read",
            "read",
            "src/**",
            PermissionDecision.Ask,
            PermissionScope.Project,
            PermissionRuleSource.UserDecision);
        var request = CreateRequest("read", "read", "src/Program.cs");

        var forward = _evaluator.Evaluate(request, [first, second], PermissionDecision.Allow);
        var reverse = _evaluator.Evaluate(request, [second, first], PermissionDecision.Allow);

        Assert.Equal(forward, reverse);
        Assert.Equal(PermissionScope.Session, forward.MatchedRule?.Scope);
    }

    [Theory]
    [InlineData("secrets/**", "secrets/private.txt", PermissionDecision.Deny)]
    [InlineData("**/*.key", "nested/keys/private.key", PermissionDecision.Deny)]
    [InlineData("src/private/*", "src/private/file.txt", PermissionDecision.Deny)]
    [InlineData("src/private/*", "src/private/nested/file.txt", PermissionDecision.Allow)]
    public void Nested_wildcards_match_project_relative_paths_deterministically(
        string pattern,
        string target,
        PermissionDecision expected)
    {
        var result = Evaluate(
            PermissionRule.Create("read", "read", pattern, PermissionDecision.Deny),
            target: target);

        Assert.Equal(expected, result.Decision);
    }

    [Fact]
    public void Missing_scope_preserves_project_default_and_invalid_scope_is_rejected()
    {
        var defaults = new PermissionOptions
        {
            Rules =
            [
                new PermissionRuleOptions
                {
                    Tool = "read",
                    Operation = "read",
                    Target = "docs/**",
                    Decision = "Ask",
                },
            ],
        };
        var rule = Assert.Single(defaults.CreateRules());
        Assert.Equal(PermissionScope.Project, rule.Scope);

        var invalid = new PermissionOptions
        {
            Rules =
            [
                new PermissionRuleOptions
                {
                    Tool = "read",
                    Operation = "read",
                    Target = "docs/**",
                    Decision = "Ask",
                    Scope = "Forever",
                },
            ],
        };
        Assert.Throws<InvalidOperationException>(invalid.Validate);
    }

    [Fact]
    public void Configuration_rejects_invalid_and_duplicate_rules()
    {
        var invalid = new PermissionOptions { DefaultDecision = "unknown" };
        Assert.Throws<InvalidOperationException>(invalid.Validate);

        var numericDecision = new PermissionOptions { DefaultDecision = "99" };
        Assert.Throws<InvalidOperationException>(numericDecision.Validate);

        var numericNamedDecision = new PermissionOptions { DefaultDecision = "0" };
        Assert.Throws<InvalidOperationException>(numericNamedDecision.Validate);

        var nullRules = new PermissionOptions { Rules = null! };
        Assert.Throws<InvalidOperationException>(nullRules.Validate);

        var duplicate = new PermissionOptions
        {
            Rules =
            [
                new PermissionRuleOptions
                {
                    Tool = "read",
                    Operation = "read",
                    Target = "src/**",
                    Decision = "Allow",
                },
                new PermissionRuleOptions
                {
                    Tool = "read",
                    Operation = "read",
                    Target = "src/**",
                    Decision = "Deny",
                },
            ],
        };
        Assert.Throws<InvalidOperationException>(duplicate.Validate);
    }

    private PermissionEvaluationResult Evaluate(
        PermissionRule first,
        PermissionRule? second = null,
        string target = "src/Program.cs")
    {
        var rules = new List<PermissionRule> { first };
        if (second is not null)
        {
            rules.Add(second);
        }

        return _evaluator.Evaluate(
            CreateRequest("read", "read", target),
            rules,
            PermissionDecision.Allow);
    }

    private static PermissionRequest CreateRequest(
        string tool,
        string operation,
        string target)
    {
        return new PermissionRequest(
            tool,
            operation,
            target,
            Directory.GetCurrentDirectory(),
            SessionId.New(),
            ProjectId.New(),
            isInteractive: true);
    }
}
