using System.Text;
using AgentPulse.Application.AgentTools;

namespace AgentPulse.Infrastructure.Mutations;

internal enum PatchCommandKind
{
    Add = 0,
    Update = 1,
    Delete = 2,
}

internal enum PatchLineKind
{
    Context = 0,
    Remove = 1,
    Add = 2,
}

internal sealed record PatchLine(PatchLineKind Kind, string Text);

internal sealed record PatchHunk(IReadOnlyList<PatchLine> Lines);

internal sealed record PatchCommand(
    PatchCommandKind Kind,
    string Path,
    string? MoveTo,
    IReadOnlyList<string> AddedLines,
    IReadOnlyList<PatchHunk> Hunks);

internal sealed record PatchDocument(IReadOnlyList<PatchCommand> Commands);

internal interface IApplyPatchParser
{
    PatchDocument Parse(string patchText);
}

internal sealed class ApplyPatchParser(MutationToolOptions options) : IApplyPatchParser
{
    private const string BeginMarker = "*** Begin Patch";
    private const string EndMarker = "*** End Patch";
    private const string AddPrefix = "*** Add File:";
    private const string UpdatePrefix = "*** Update File:";
    private const string DeletePrefix = "*** Delete File:";
    private const string MovePrefix = "*** Move to:";

    public PatchDocument Parse(string patchText)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(patchText))
        {
            throw new MutationValidationException("The patch must not be empty.");
        }

        if (Encoding.UTF8.GetByteCount(patchText) > options.MaxPatchBytes)
        {
            throw new MutationValidationException(
                $"The patch exceeds the maximum size of {options.MaxPatchBytes} bytes.");
        }

        var normalized = patchText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim('\n');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || lines[0] != BeginMarker)
        {
            throw new MutationValidationException("The patch is missing the '*** Begin Patch' marker.");
        }

        if (lines[^1] != EndMarker)
        {
            throw new MutationValidationException("The patch is missing the '*** End Patch' marker.");
        }

        var commands = new List<PatchCommand>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var index = 1;
        while (index < lines.Length - 1)
        {
            var line = lines[index];
            if (line.StartsWith(AddPrefix, StringComparison.Ordinal))
            {
                var path = ParsePath(line, AddPrefix);
                RegisterPath(paths, path);
                index++;
                var added = new List<string>();
                while (index < lines.Length - 1 && !IsCommand(lines[index]))
                {
                    if (!lines[index].StartsWith('+'))
                    {
                        throw new MutationValidationException(
                            $"Malformed add-file content for '{path}'. Every content line must start with '+'.");
                    }

                    added.Add(lines[index][1..]);
                    index++;
                }

                commands.Add(new PatchCommand(
                    PatchCommandKind.Add,
                    path,
                    null,
                    added,
                    Array.Empty<PatchHunk>()));
                continue;
            }

            if (line.StartsWith(DeletePrefix, StringComparison.Ordinal))
            {
                var path = ParsePath(line, DeletePrefix);
                RegisterPath(paths, path);
                commands.Add(new PatchCommand(
                    PatchCommandKind.Delete,
                    path,
                    null,
                    Array.Empty<string>(),
                    Array.Empty<PatchHunk>()));
                index++;
                if (index < lines.Length - 1 && !IsCommand(lines[index]))
                {
                    throw new MutationValidationException(
                        $"Malformed delete-file command for '{path}'.");
                }

                continue;
            }

            if (line.StartsWith(UpdatePrefix, StringComparison.Ordinal))
            {
                var path = ParsePath(line, UpdatePrefix);
                RegisterPath(paths, path);
                index++;
                string? moveTo = null;
                if (index < lines.Length - 1 &&
                    lines[index].StartsWith(MovePrefix, StringComparison.Ordinal))
                {
                    moveTo = ParsePath(lines[index], MovePrefix);
                    RegisterPath(paths, moveTo);
                    index++;
                }

                var hunks = new List<PatchHunk>();
                while (index < lines.Length - 1 && !IsCommand(lines[index]))
                {
                    if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
                    {
                        if (lines[index].StartsWith("***", StringComparison.Ordinal))
                        {
                            throw new MutationValidationException(
                                $"Unknown patch command '{lines[index]}'.");
                        }

                        throw new MutationValidationException(
                            $"Malformed update hunk for '{path}'. Expected an '@@' header.");
                    }

                    index++;
                    var hunkLines = new List<PatchLine>();
                    while (index < lines.Length - 1 &&
                           !lines[index].StartsWith("@@", StringComparison.Ordinal) &&
                           !IsCommand(lines[index]))
                    {
                        var hunkLine = lines[index];
                        if (hunkLine.Length == 0)
                        {
                            throw new MutationValidationException(
                                $"Malformed update hunk for '{path}'. Hunk lines require a prefix.");
                        }

                        var kind = hunkLine[0] switch
                        {
                            ' ' => PatchLineKind.Context,
                            '-' => PatchLineKind.Remove,
                            '+' => PatchLineKind.Add,
                            _ => throw new MutationValidationException(
                                $"Malformed update hunk for '{path}'. Invalid hunk line prefix '{hunkLine[0]}'."),
                        };
                        hunkLines.Add(new PatchLine(kind, hunkLine[1..]));
                        index++;
                    }

                    if (hunkLines.Count == 0)
                    {
                        throw new MutationValidationException(
                            $"Malformed update hunk for '{path}'. The hunk is empty.");
                    }

                    if (hunkLines.All(static value => value.Kind == PatchLineKind.Context))
                    {
                        throw new MutationValidationException(
                            $"Malformed update hunk for '{path}'. The hunk does not contain a change.");
                    }

                    hunks.Add(new PatchHunk(hunkLines));
                }

                if (hunks.Count == 0 && moveTo is null)
                {
                    throw new MutationValidationException(
                        $"The update command for '{path}' does not contain a hunk.");
                }

                commands.Add(new PatchCommand(
                    PatchCommandKind.Update,
                    path,
                    moveTo,
                    Array.Empty<string>(),
                    hunks));
                continue;
            }

            if (line.StartsWith("***", StringComparison.Ordinal))
            {
                throw new MutationValidationException($"Unknown patch command '{line}'.");
            }

            throw new MutationValidationException(
                $"Malformed patch content at line {index + 1}.");
        }

        if (commands.Count == 0)
        {
            throw new MutationValidationException("The patch does not contain any file operations.");
        }

        return new PatchDocument(commands);
    }

    private static bool IsCommand(string line) =>
        line.StartsWith(AddPrefix, StringComparison.Ordinal) ||
        line.StartsWith(UpdatePrefix, StringComparison.Ordinal) ||
        line.StartsWith(DeletePrefix, StringComparison.Ordinal) ||
        line == EndMarker;

    private static string ParsePath(string line, string prefix)
    {
        var value = line[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MutationValidationException("Patch file paths must not be empty.");
        }

        value = value.Replace('\\', '/');
        if (Path.IsPathRooted(value) ||
            value.StartsWith('/') ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
        {
            throw new MutationValidationException("Patch file paths must be workspace-relative.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
        {
            throw new MutationValidationException("Patch file paths must not contain traversal.");
        }

        return string.Join('/', segments);
    }

    private static void RegisterPath(HashSet<string> paths, string path)
    {
        if (!paths.Add(path))
        {
            throw new MutationValidationException(
                $"The patch contains conflicting operations for '{path}'.");
        }
    }
}
