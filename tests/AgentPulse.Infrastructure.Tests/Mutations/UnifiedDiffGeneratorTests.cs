using AgentPulse.Infrastructure.Mutations;
using static AgentPulse.Infrastructure.Tests.Mutations.MutationTestSupport;

namespace AgentPulse.Infrastructure.Tests.Mutations;

public sealed class UnifiedDiffGeneratorTests
{
    [Fact]
    public void Update_diff_is_deterministic_and_counts_only_changed_lines()
    {
        var generator = new UnifiedDiffGenerator(CreateOptions());
        const string before = "one\ntwo\nunchanged\nthree\nfour\n";
        const string after = "one\nchanged-two\nunchanged\nchanged-three\nfour\n";

        var first = generator.CreateUpdate("src\\Example.cs", before, after);
        var second = generator.CreateUpdate("src/Example.cs", before, after);

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(2, first.Additions);
        Assert.Equal(2, first.Deletions);
        Assert.Contains("--- a/src/Example.cs", first.Text, StringComparison.Ordinal);
        Assert.Contains("+++ b/src/Example.cs", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', first.Text);
    }

    [Fact]
    public void Add_delete_and_move_have_clear_headers()
    {
        var generator = new UnifiedDiffGenerator(CreateOptions());

        var add = generator.CreateAdd("new.txt", "one\n");
        var delete = generator.CreateDelete("old.txt", "one\n");
        var move = generator.CreateMove("old.txt", "new.txt", "one\n", "two\n");

        Assert.Contains("--- /dev/null", add.Text, StringComparison.Ordinal);
        Assert.Contains("+++ b/new.txt", add.Text, StringComparison.Ordinal);
        Assert.Contains("--- a/old.txt", delete.Text, StringComparison.Ordinal);
        Assert.Contains("+++ /dev/null", delete.Text, StringComparison.Ordinal);
        Assert.StartsWith("rename from old.txt\nrename to new.txt\n", move.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_context_lines_are_applied()
    {
        var options = CreateOptions();
        options.DiffContextLines = 1;
        var generator = new UnifiedDiffGenerator(options);

        var diff = generator.CreateUpdate(
            "file.txt",
            "zero\none\ntwo\nthree\nfour\n",
            "zero\none\nchanged\nthree\nfour\n");

        Assert.Contains(" one\n", diff.Text, StringComparison.Ordinal);
        Assert.Contains(" three\n", diff.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(" zero\n", diff.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(" four\n", diff.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_is_truncated_without_changing_full_counts()
    {
        var options = CreateOptions();
        options.MaxDiffPreviewCharacters = 120;
        var generator = new UnifiedDiffGenerator(options);
        var before = string.Join('\n', Enumerable.Range(0, 50).Select(static value => $"old-{value}"));
        var after = string.Join('\n', Enumerable.Range(0, 50).Select(static value => $"new-{value}"));

        var diff = generator.CreateUpdate("file.txt", before, after);

        Assert.True(diff.WasPreviewTruncated);
        Assert.True(diff.Preview.Length <= options.MaxDiffPreviewCharacters);
        Assert.Contains("preview truncated", diff.Preview, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(50, diff.Additions);
        Assert.Equal(50, diff.Deletions);
        Assert.True(diff.Text.Length > diff.Preview.Length);
    }

    [Fact]
    public void CrLf_is_normalized_only_for_display()
    {
        var generator = new UnifiedDiffGenerator(CreateOptions());

        var diff = generator.CreateUpdate("file.txt", "one\r\ntwo\r\n", "one\r\nchanged\r\n");

        Assert.DoesNotContain('\r', diff.Text);
        Assert.Equal(1, diff.Additions);
        Assert.Equal(1, diff.Deletions);
    }

    [Fact]
    public void Trailing_newline_change_is_represented_deterministically()
    {
        var generator = new UnifiedDiffGenerator(CreateOptions());

        var diff = generator.CreateUpdate("file.txt", "one", "one\n");

        Assert.Equal(1, diff.Additions);
        Assert.Equal(1, diff.Deletions);
        Assert.Contains("No newline at end of file", diff.Text, StringComparison.Ordinal);
    }
    [Fact]
    public void Large_diff_uses_bounded_linear_fallback_with_accurate_counts()
    {
        var generator = new UnifiedDiffGenerator(CreateOptions());
        var before = string.Join('\n', Enumerable.Range(0, 1_100).Select(static value => $"old-{value}"));
        var after = string.Join('\n', Enumerable.Range(0, 1_100).Select(static value => $"new-{value}"));

        var diff = generator.CreateUpdate("large.txt", before, after);

        Assert.Equal(1_100, diff.Additions);
        Assert.Equal(1_100, diff.Deletions);
        Assert.Contains("--- a/large.txt", diff.Text, StringComparison.Ordinal);
        Assert.Contains("+++ b/large.txt", diff.Text, StringComparison.Ordinal);
    }

}
