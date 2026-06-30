using System.Globalization;
using System.Text;
using AgentPulse.Application.AgentTools;

namespace AgentPulse.Infrastructure.Mutations;

internal sealed class UnifiedDiffGenerator : IUnifiedDiffGenerator
{
    private const int MaximumMyersLineCount = 2_000;

    private readonly int _contextLines;
    private readonly int _maxPreviewCharacters;

    public UnifiedDiffGenerator(MutationToolOptions options)
    {
        options.Validate();
        _contextLines = options.DiffContextLines;
        _maxPreviewCharacters = options.MaxDiffPreviewCharacters;
    }

    public UnifiedDiffResult CreateAdd(string relativePath, string newText)
    {
        var path = NormalizePath(relativePath);
        var result = CreateCore("/dev/null", $"b/{path}", string.Empty, newText);
        return string.IsNullOrEmpty(result.Text)
            ? CreateResult($"--- /dev/null\n+++ b/{path}\n", 0, 0)
            : result;
    }

    public UnifiedDiffResult CreateUpdate(string relativePath, string oldText, string newText)
    {
        var path = NormalizePath(relativePath);
        return CreateCore($"a/{path}", $"b/{path}", oldText, newText);
    }

    public UnifiedDiffResult CreateDelete(string relativePath, string oldText)
    {
        var path = NormalizePath(relativePath);
        var result = CreateCore($"a/{path}", "/dev/null", oldText, string.Empty);
        return string.IsNullOrEmpty(result.Text)
            ? CreateResult($"--- a/{path}\n+++ /dev/null\n", 0, 0)
            : result;
    }

    public UnifiedDiffResult CreateMove(
        string oldRelativePath,
        string newRelativePath,
        string oldText,
        string newText)
    {
        var oldPath = NormalizePath(oldRelativePath);
        var newPath = NormalizePath(newRelativePath);
        var content = CreateCore($"a/{oldPath}", $"b/{newPath}", oldText, newText);
        var prefix = $"rename from {oldPath}\nrename to {newPath}\n";
        return CreateResult(
            prefix + content.Text,
            content.Additions,
            content.Deletions);
    }

    public UnifiedDiffResult Combine(IEnumerable<UnifiedDiffResult> diffs)
    {
        ArgumentNullException.ThrowIfNull(diffs);
        var values = diffs.Where(static diff => !string.IsNullOrEmpty(diff.Text)).ToArray();
        var text = string.Join("\n", values.Select(static diff => diff.Text.TrimEnd('\n')));
        if (text.Length > 0)
        {
            text += "\n";
        }

        return CreateResult(
            text,
            values.Sum(static diff => diff.Additions),
            values.Sum(static diff => diff.Deletions));
    }

    private UnifiedDiffResult CreateCore(
        string oldHeader,
        string newHeader,
        string oldText,
        string newText)
    {
        var oldFile = DiffFile.Parse(oldText);
        var newFile = DiffFile.Parse(newText);
        if (oldFile.Lines.Count + newFile.Lines.Count > MaximumMyersLineCount)
        {
            return CreateLinearFallback(oldHeader, newHeader, oldFile, newFile);
        }

        var operations = ComputeDiff(oldFile.Lines, newFile.Lines).ToList();
        if (oldFile.HasTrailingNewline != newFile.HasTrailingNewline &&
            oldFile.Lines.Count == newFile.Lines.Count &&
            oldFile.Lines.Count > 0 &&
            operations.All(static operation => operation.Kind == DiffOperationKind.Equal))
        {
            operations.RemoveAt(operations.Count - 1);
            var finalLine = oldFile.Lines[^1];
            operations.Add(new DiffOperation(DiffOperationKind.Delete, finalLine));
            operations.Add(new DiffOperation(DiffOperationKind.Insert, finalLine));
        }

        if (operations.All(static operation => operation.Kind == DiffOperationKind.Equal))
        {
            return CreateResult(string.Empty, 0, 0);
        }

        var annotated = Annotate(operations);
        var ranges = BuildHunkRanges(annotated);
        var builder = new StringBuilder();
        builder.Append("--- ").Append(oldHeader).Append('\n');
        builder.Append("+++ ").Append(newHeader).Append('\n');

        foreach (var range in ranges)
        {
            var values = annotated[range.Start..(range.End + 1)];
            var oldCount = values.Count(static value => value.Operation.Kind != DiffOperationKind.Insert);
            var newCount = values.Count(static value => value.Operation.Kind != DiffOperationKind.Delete);
            var oldStart = values[0].OldLine;
            var newStart = values[0].NewLine;
            if (oldCount == 0)
            {
                oldStart--;
            }

            if (newCount == 0)
            {
                newStart--;
            }

            builder.Append("@@ -")
                .Append(FormatRange(oldStart, oldCount))
                .Append(" +")
                .Append(FormatRange(newStart, newCount))
                .Append(" @@\n");

            foreach (var value in values)
            {
                builder.Append(value.Operation.Kind switch
                {
                    DiffOperationKind.Equal => ' ',
                    DiffOperationKind.Delete => '-',
                    DiffOperationKind.Insert => '+',
                    _ => throw new InvalidOperationException("Diff operation kind is invalid."),
                });
                builder.Append(value.Operation.Text).Append('\n');

                if (value.Operation.Kind != DiffOperationKind.Insert &&
                    value.OldLine == oldFile.Lines.Count &&
                    !oldFile.HasTrailingNewline)
                {
                    builder.Append("\\ No newline at end of file\n");
                }

                if (value.Operation.Kind == DiffOperationKind.Insert &&
                    value.NewLine == newFile.Lines.Count &&
                    !newFile.HasTrailingNewline)
                {
                    builder.Append("\\ No newline at end of file\n");
                }
            }
        }

        return CreateResult(
            builder.ToString(),
            operations.Count(static operation => operation.Kind == DiffOperationKind.Insert),
            operations.Count(static operation => operation.Kind == DiffOperationKind.Delete));
    }

    private UnifiedDiffResult CreateLinearFallback(
        string oldHeader,
        string newHeader,
        DiffFile oldFile,
        DiffFile newFile)
    {
        var prefix = 0;
        var shared = Math.Min(oldFile.Lines.Count, newFile.Lines.Count);
        while (prefix < shared && string.Equals(
                   oldFile.Lines[prefix],
                   newFile.Lines[prefix],
                   StringComparison.Ordinal))
        {
            prefix++;
        }

        var oldSuffix = oldFile.Lines.Count;
        var newSuffix = newFile.Lines.Count;
        while (oldSuffix > prefix && newSuffix > prefix && string.Equals(
                   oldFile.Lines[oldSuffix - 1],
                   newFile.Lines[newSuffix - 1],
                   StringComparison.Ordinal))
        {
            oldSuffix--;
            newSuffix--;
        }

        if (prefix == oldFile.Lines.Count &&
            prefix == newFile.Lines.Count &&
            oldFile.HasTrailingNewline == newFile.HasTrailingNewline)
        {
            return CreateResult(string.Empty, 0, 0);
        }

        if (prefix == oldFile.Lines.Count &&
            prefix == newFile.Lines.Count &&
            prefix > 0)
        {
            prefix--;
            oldSuffix = oldFile.Lines.Count;
            newSuffix = newFile.Lines.Count;
        }

        var contextStart = Math.Max(0, prefix - _contextLines);
        var commonSuffix = Math.Min(
            oldFile.Lines.Count - oldSuffix,
            newFile.Lines.Count - newSuffix);
        var contextAfter = Math.Min(_contextLines, commonSuffix);
        var oldChanged = oldSuffix - prefix;
        var newChanged = newSuffix - prefix;
        var leadingContext = prefix - contextStart;
        var oldCount = leadingContext + oldChanged + contextAfter;
        var newCount = leadingContext + newChanged + contextAfter;
        var oldStart = contextStart + 1;
        var newStart = contextStart + 1;
        if (oldCount == 0)
        {
            oldStart--;
        }

        if (newCount == 0)
        {
            newStart--;
        }

        var builder = new StringBuilder();
        builder.Append("--- ").Append(oldHeader).Append('\n');
        builder.Append("+++ ").Append(newHeader).Append('\n');
        builder.Append("@@ -")
            .Append(FormatRange(oldStart, oldCount))
            .Append(" +")
            .Append(FormatRange(newStart, newCount))
            .Append(" @@\n");

        for (var index = contextStart; index < prefix; index++)
        {
            AppendFallbackLine(
                builder,
                ' ',
                oldFile.Lines[index],
                index,
                index,
                oldFile,
                newFile);
        }

        for (var index = prefix; index < oldSuffix; index++)
        {
            AppendFallbackLine(
                builder,
                '-',
                oldFile.Lines[index],
                index,
                null,
                oldFile,
                newFile);
        }

        for (var index = prefix; index < newSuffix; index++)
        {
            AppendFallbackLine(
                builder,
                '+',
                newFile.Lines[index],
                null,
                index,
                oldFile,
                newFile);
        }

        for (var offset = 0; offset < contextAfter; offset++)
        {
            var oldIndex = oldSuffix + offset;
            var newIndex = newSuffix + offset;
            AppendFallbackLine(
                builder,
                ' ',
                oldFile.Lines[oldIndex],
                oldIndex,
                newIndex,
                oldFile,
                newFile);
        }

        return CreateResult(builder.ToString(), newChanged, oldChanged);
    }

    private static void AppendFallbackLine(
        StringBuilder builder,
        char prefix,
        string text,
        int? oldIndex,
        int? newIndex,
        DiffFile oldFile,
        DiffFile newFile)
    {
        builder.Append(prefix).Append(text).Append('\n');
        var oldMissingNewline = oldIndex == oldFile.Lines.Count - 1 &&
                                !oldFile.HasTrailingNewline;
        var newMissingNewline = newIndex == newFile.Lines.Count - 1 &&
                                !newFile.HasTrailingNewline;
        if (oldMissingNewline || newMissingNewline)
        {
            builder.Append("\\ No newline at end of file\n");
        }
    }

    private static IReadOnlyList<DiffOperation> ComputeDiff(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines)
    {
        var maximum = oldLines.Count + newLines.Count;
        var offset = maximum + 1;
        var width = maximum * 2 + 3;
        var frontier = new int[width];
        Array.Fill(frontier, -1);
        frontier[offset + 1] = 0;
        var trace = new List<int[]>(maximum + 1);

        for (var distance = 0; distance <= maximum; distance++)
        {
            trace.Add((int[])frontier.Clone());
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var index = offset + diagonal;
                int x;
                if (diagonal == -distance ||
                    (diagonal != distance && frontier[index - 1] < frontier[index + 1]))
                {
                    x = frontier[index + 1];
                }
                else
                {
                    x = frontier[index - 1] + 1;
                }

                var y = x - diagonal;
                while (x < oldLines.Count && y < newLines.Count &&
                       string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                frontier[index] = x;
                if (x >= oldLines.Count && y >= newLines.Count)
                {
                    return Backtrack(trace, oldLines, newLines, distance, offset);
                }
            }
        }

        throw new InvalidOperationException("Unable to generate a deterministic unified diff.");
    }

    private static IReadOnlyList<DiffOperation> Backtrack(
        IReadOnlyList<int[]> trace,
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        int finalDistance,
        int offset)
    {
        var operations = new List<DiffOperation>(oldLines.Count + newLines.Count);
        var x = oldLines.Count;
        var y = newLines.Count;
        for (var distance = finalDistance; distance >= 0; distance--)
        {
            var frontier = trace[distance];
            var diagonal = x - y;
            int previousDiagonal;
            if (diagonal == -distance ||
                (diagonal != distance &&
                 frontier[offset + diagonal - 1] < frontier[offset + diagonal + 1]))
            {
                previousDiagonal = diagonal + 1;
            }
            else
            {
                previousDiagonal = diagonal - 1;
            }

            var previousX = frontier[offset + previousDiagonal];
            var previousY = previousX - previousDiagonal;
            while (x > previousX && y > previousY)
            {
                operations.Add(new DiffOperation(DiffOperationKind.Equal, oldLines[x - 1]));
                x--;
                y--;
            }

            if (distance == 0)
            {
                break;
            }

            if (x == previousX)
            {
                operations.Add(new DiffOperation(DiffOperationKind.Insert, newLines[y - 1]));
                y--;
            }
            else
            {
                operations.Add(new DiffOperation(DiffOperationKind.Delete, oldLines[x - 1]));
                x--;
            }
        }

        operations.Reverse();
        return operations;
    }

    private static AnnotatedOperation[] Annotate(IReadOnlyList<DiffOperation> operations)
    {
        var values = new AnnotatedOperation[operations.Count];
        var oldLine = 1;
        var newLine = 1;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            values[index] = new AnnotatedOperation(operation, oldLine, newLine);
            if (operation.Kind != DiffOperationKind.Insert)
            {
                oldLine++;
            }

            if (operation.Kind != DiffOperationKind.Delete)
            {
                newLine++;
            }
        }

        return values;
    }

    private IReadOnlyList<HunkRange> BuildHunkRanges(IReadOnlyList<AnnotatedOperation> operations)
    {
        var changed = operations
            .Select((operation, index) => (operation, index))
            .Where(static value => value.operation.Operation.Kind != DiffOperationKind.Equal)
            .Select(static value => value.index)
            .ToArray();
        var ranges = new List<HunkRange>();
        foreach (var index in changed)
        {
            var start = index;
            var context = _contextLines;
            while (start > 0 && context > 0)
            {
                start--;
                if (operations[start].Operation.Kind == DiffOperationKind.Equal)
                {
                    context--;
                }
            }

            var end = index;
            context = _contextLines;
            while (end + 1 < operations.Count && context > 0)
            {
                end++;
                if (operations[end].Operation.Kind == DiffOperationKind.Equal)
                {
                    context--;
                }
            }

            if (ranges.Count > 0 && start <= ranges[^1].End + 1)
            {
                ranges[^1] = ranges[^1] with { End = Math.Max(ranges[^1].End, end) };
            }
            else
            {
                ranges.Add(new HunkRange(start, end));
            }
        }

        return ranges;
    }

    private UnifiedDiffResult CreateResult(string text, int additions, int deletions)
    {
        if (text.Length <= _maxPreviewCharacters)
        {
            return new UnifiedDiffResult(text, text, additions, deletions, false);
        }

        var notice =
            $"\n[Diff preview truncated at {_maxPreviewCharacters.ToString(CultureInfo.InvariantCulture)} characters. Full additions and deletions counts remain available.]\n";
        var available = Math.Max(0, _maxPreviewCharacters - notice.Length);
        var preview = text[..available] + notice;
        return new UnifiedDiffResult(text, preview, additions, deletions, true);
    }

    private static string FormatRange(int start, int count) => count == 1
        ? start.ToString(CultureInfo.InvariantCulture)
        : $"{start.ToString(CultureInfo.InvariantCulture)},{count.ToString(CultureInfo.InvariantCulture)}";

    private static string NormalizePath(string value) => value.Replace('\\', '/').TrimStart('/');

    private enum DiffOperationKind
    {
        Equal = 0,
        Delete = 1,
        Insert = 2,
    }

    private sealed record DiffOperation(DiffOperationKind Kind, string Text);

    private sealed record AnnotatedOperation(
        DiffOperation Operation,
        int OldLine,
        int NewLine);

    private sealed record HunkRange(int Start, int End);

    private sealed record DiffFile(IReadOnlyList<string> Lines, bool HasTrailingNewline)
    {
        public static DiffFile Parse(string value)
        {
            var normalized = TextFileCodec.NormalizeForDiff(value);
            if (normalized.Length == 0)
            {
                return new DiffFile(Array.Empty<string>(), false);
            }

            var trailing = normalized.EndsWith('\n');
            var content = trailing ? normalized[..^1] : normalized;
            var lines = content.Length == 0
                ? new[] { string.Empty }
                : content.Split('\n');
            return new DiffFile(lines, trailing);
        }
    }
}
