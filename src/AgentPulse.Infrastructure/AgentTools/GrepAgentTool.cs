using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Infrastructure.AgentTools;

public sealed class GrepAgentTool(
    IWorkspacePathResolver pathResolver,
    AgentToolOptions options,
    ILogger<GrepAgentTool>? logger = null) : IAgentTool, IPermissionAwareAgentTool
{
    private const int MaximumRenderedLineLength = 500;
    private readonly ILogger<GrepAgentTool> _logger = logger ?? NullLogger<GrepAgentTool>.Instance;

    public string Name => "grep";

    public string Description =>
        "Search text files inside the active workspace using a regular expression and optional include glob.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"pattern":{"type":"string"},"basePath":{"type":"string"},"include":{"type":"string"},"caseSensitive":{"type":"boolean"},"maxResults":{"type":"integer","minimum":1}},"required":["pattern"],"additionalProperties":false}
        """;

    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        options.Validate();

        GrepArguments input;
        try
        {
            input = ToolArgumentReader.Deserialize<GrepArguments>(arguments);
        }
        catch (ArgumentException exception)
        {
            return DeterministicFailure(exception.Message);
        }

        if (string.IsNullOrWhiteSpace(input.Pattern))
        {
            return DeterministicFailure("The grep tool requires a regular expression pattern.");
        }

        Regex regex;
        GlobPattern? include = null;
        string basePath;
        try
        {
            regex = new Regex(
                input.Pattern,
                RegexOptions.CultureInvariant |
                (input.CaseSensitive == true ? RegexOptions.None : RegexOptions.IgnoreCase),
                TimeSpan.FromSeconds(2));
            if (!string.IsNullOrWhiteSpace(input.Include))
            {
                include = GlobPattern.Create(input.Include);
            }

            basePath = pathResolver.Resolve(context.WorkspaceRoot, input.BasePath);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return DeterministicFailure(exception.Message);
        }

        if (!Directory.Exists(basePath) && !File.Exists(basePath))
        {
            return DeterministicFailure("The grep base path does not exist.");
        }

        var limit = Math.Min(input.MaxResults ?? options.MaxGrepResults, options.MaxGrepResults);
        if (limit <= 0)
        {
            return DeterministicFailure("Grep maxResults must be greater than zero.");
        }

        var results = new List<string>();
        var totalMatches = 0;
        foreach (var file in WorkspaceFileEnumerator.EnumerateFiles(basePath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeToBase = Path.GetRelativePath(basePath, file).Replace('\\', '/');
            if (include is not null && !include.IsMatch(relativeToBase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(context.WorkspaceRoot, file).Replace('\\', '/');
            var authorization = await context.AuthorizeResourceAsync(
                "search",
                relative,
                $"Search workspace file '{relative}'.",
                cancellationToken);
            if (!authorization.IsAllowed)
            {
                if (authorization.IsExplicitlyDenied)
                {
                    _logger.LogInformation(
                        "Resource excluded because of explicit deny before grep content access for target {Target}.",
                        relative);
                    continue;
                }

                _logger.LogWarning(
                    "Resource permission evaluation failed for grep target {Target}.",
                    relative);
                _logger.LogWarning(
                    "Grep tool aborted because resource permission could not be resolved for target {Target}.",
                    relative);
                return authorization.Failure ?? AgentToolResult.Failure(
                    $"Resource permission evaluation failed for '{relative}'.");
            }

            var info = new FileInfo(file);
            if (info.Length > options.MaxGrepFileBytes || await IsBinaryAsync(file, cancellationToken))
            {
                continue;
            }

            var lineNumber = 0;
            using var reader = new StreamReader(
                file,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                bool matched;
                try
                {
                    matched = regex.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    return AgentToolResult.Failure(
                        "The grep regular expression exceeded its match timeout.",
                        classification: AgentToolFailureClassification.Transient);
                }

                if (!matched)
                {
                    continue;
                }

                totalMatches++;
                if (results.Count < limit)
                {
                    var rendered = line.Length > MaximumRenderedLineLength
                        ? line[..MaximumRenderedLineLength] + "..."
                        : line;
                    results.Add($"{relative}:{lineNumber}: {rendered}");
                }
            }
        }

        var output = results.Count == 0 ? "No matches found." : string.Join("\n", results);
        var truncated = totalMatches > results.Count;
        if (truncated)
        {
            output += $"\n\n[Results truncated: showing {results.Count} of {totalMatches} matches.]";
        }

        return AgentToolResult.Success(
            output,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["matches"] = totalMatches.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["truncated"] = truncated.ToString().ToLowerInvariant(),
            });
    }

    public PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context)
    {
        GrepArguments input;
        try
        {
            input = ToolArgumentReader.Deserialize<GrepArguments>(arguments);
        }
        catch (ArgumentException exception)
        {
            return PermissionTargetResolution.Reject(DeterministicFailure(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(input.Pattern))
        {
            return PermissionTargetResolution.Reject(
                DeterministicFailure("The grep tool requires a regular expression pattern."));
        }

        try
        {
            _ = new Regex(
                input.Pattern,
                RegexOptions.CultureInvariant |
                (input.CaseSensitive == true ? RegexOptions.None : RegexOptions.IgnoreCase),
                TimeSpan.FromSeconds(2));
            if (!string.IsNullOrWhiteSpace(input.Include))
            {
                _ = GlobPattern.Create(input.Include);
            }

            var basePath = pathResolver.Resolve(context.WorkspaceRoot, input.BasePath);
            if (!Directory.Exists(basePath) && !File.Exists(basePath))
            {
                return PermissionTargetResolution.Reject(
                    DeterministicFailure("The grep base path does not exist."));
            }

            var limit = Math.Min(input.MaxResults ?? options.MaxGrepResults, options.MaxGrepResults);
            if (limit <= 0)
            {
                return PermissionTargetResolution.Reject(
                    DeterministicFailure("Grep maxResults must be greater than zero."));
            }

            var relativeBase = Path.GetRelativePath(context.WorkspaceRoot, basePath).Replace('\\', '/');
            var include = string.IsNullOrWhiteSpace(input.Include)
                ? "**"
                : input.Include.Trim().Replace('\\', '/').TrimStart('/');
            var target = relativeBase == "."
                ? include
                : $"{relativeBase}/{include}";
            return PermissionTargetResolution.Success(
                "search",
                target,
                $"Search workspace text within '{target}'.");
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return PermissionTargetResolution.Reject(DeterministicFailure(exception.Message));
        }
    }

    private static AgentToolResult DeterministicFailure(string error) =>
        AgentToolResult.Failure(
            error,
            classification: AgentToolFailureClassification.Deterministic);

    private static async Task<bool> IsBinaryAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.AsSpan(0, read).IndexOf((byte)0) >= 0;
    }

    private sealed record GrepArguments(
        string? Pattern,
        string? BasePath,
        string? Include,
        bool? CaseSensitive,
        int? MaxResults);
}
