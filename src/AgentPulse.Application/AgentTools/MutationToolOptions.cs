namespace AgentPulse.Application.AgentTools;

public sealed class MutationToolOptions
{
    public const string SectionName = "AgentPulse:Tools:Mutation";

    public long MaxFileBytes { get; set; } = 5 * 1024 * 1024;

    public long MaxPatchBytes { get; set; } = 1024 * 1024;

    public int MaxDiffPreviewCharacters { get; set; } = 12_000;

    public int DiffContextLines { get; set; } = 3;

    public List<string> ProtectedPatterns { get; set; } = [];

    public void Validate()
    {
        if (MaxFileBytes <= 0 || MaxPatchBytes <= 0 ||
            MaxDiffPreviewCharacters <= 0 || DiffContextLines <= 0)
        {
            throw new InvalidOperationException("Mutation tool limits must be greater than zero.");
        }

        if (ProtectedPatterns is null)
        {
            throw new InvalidOperationException($"{SectionName}:ProtectedPatterns must be an array.");
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var unique = new HashSet<string>(comparer);
        for (var index = 0; index < ProtectedPatterns.Count; index++)
        {
            var pattern = ProtectedPatterns[index];
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:ProtectedPatterns:{index} must not be empty.");
            }

            var normalized = pattern.Trim().Replace('\\', '/');
            if (IsPortableRooted(normalized) ||
                normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(static segment => segment == ".."))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:ProtectedPatterns:{index} must be workspace-relative and must not contain traversal.");
            }

            if (!unique.Add(normalized))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:ProtectedPatterns contains duplicate pattern '{normalized}'.");
            }
        }
    }

    private static bool IsPortableRooted(string path) =>
        Path.IsPathRooted(path) ||
        path.StartsWith('/') ||
        path.StartsWith("//", StringComparison.Ordinal) ||
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');
}
