using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPulse.Infrastructure.AgentTools;

public sealed class GlobAgentTool(
    IWorkspacePathResolver pathResolver,
    AgentToolOptions options,
    ILogger<GlobAgentTool>? logger = null) : IAgentTool, IPermissionAwareAgentTool
{
    private readonly ILogger<GlobAgentTool> _logger = logger ?? NullLogger<GlobAgentTool>.Instance;

    public string Name => "glob";

    public string Description =>
        "Find files inside the active workspace using a cross-platform glob pattern such as **/*.cs.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"pattern":{"type":"string"},"basePath":{"type":"string"},"maxResults":{"type":"integer","minimum":1}},"required":["pattern"],"additionalProperties":false}
        """;

    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        options.Validate();

        GlobArguments input;
        try
        {
            input = ToolArgumentReader.Deserialize<GlobArguments>(arguments);
        }
        catch (ArgumentException exception)
        {
            return DeterministicFailure(exception.Message);
        }

        if (string.IsNullOrWhiteSpace(input.Pattern))
        {
            return DeterministicFailure("The glob tool requires a pattern.");
        }

        GlobPattern matcher;
        string basePath;
        try
        {
            matcher = GlobPattern.Create(input.Pattern);
            basePath = pathResolver.Resolve(context.WorkspaceRoot, input.BasePath);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return DeterministicFailure(exception.Message);
        }

        if (!Directory.Exists(basePath) && !File.Exists(basePath))
        {
            return DeterministicFailure("The glob base path does not exist.");
        }

        var limit = Math.Min(input.MaxResults ?? options.MaxGlobResults, options.MaxGlobResults);
        if (limit <= 0)
        {
            return DeterministicFailure("Glob maxResults must be greater than zero.");
        }

        var candidates = WorkspaceFileEnumerator
            .EnumerateFiles(basePath, cancellationToken)
            .Select(path => new
            {
                FullPath = path,
                RelativeToBase = Path.GetRelativePath(basePath, path).Replace('\\', '/'),
                RelativeToWorkspace = Path.GetRelativePath(context.WorkspaceRoot, path).Replace('\\', '/'),
            })
            .Where(value => matcher.IsMatch(value.RelativeToBase))
            .GroupBy(value => value.FullPath, WorkspaceFileEnumerator.GetPathComparer())
            .Select(static group => group.First())
            .OrderBy(static value => value.RelativeToWorkspace, WorkspaceFileEnumerator.GetPathComparer())
            .ToArray();

        var matches = new List<string>(Math.Min(candidates.Length, limit));
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = await context.AuthorizeResourceAsync(
                "search",
                candidate.RelativeToWorkspace,
                $"Include workspace path '{candidate.RelativeToWorkspace}' in glob results.",
                cancellationToken);
            if (!authorization.IsAllowed)
            {
                if (authorization.IsExplicitlyDenied)
                {
                    _logger.LogInformation(
                        "Resource excluded because of explicit deny for glob target {Target}.",
                        candidate.RelativeToWorkspace);
                    continue;
                }

                _logger.LogWarning(
                    "Resource permission evaluation failed for glob target {Target}.",
                    candidate.RelativeToWorkspace);
                _logger.LogWarning(
                    "Glob tool aborted because resource permission could not be resolved for target {Target}.",
                    candidate.RelativeToWorkspace);
                return authorization.Failure ?? AgentToolResult.Failure(
                    $"Resource permission evaluation failed for '{candidate.RelativeToWorkspace}'.");
            }

            matches.Add(candidate.RelativeToWorkspace);
        }

        var output = matches.Count == 0
            ? "No files found."
            : string.Join("\n", matches.Take(limit));
        var truncated = matches.Count > limit;
        if (truncated)
        {
            output += $"\n\n[Results truncated: showing {limit} of {matches.Count} files.]";
        }

        return AgentToolResult.Success(
            output,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["matches"] = matches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["truncated"] = truncated.ToString().ToLowerInvariant(),
            });
    }

    public PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context)
    {
        GlobArguments input;
        try
        {
            input = ToolArgumentReader.Deserialize<GlobArguments>(arguments);
        }
        catch (ArgumentException exception)
        {
            return PermissionTargetResolution.Reject(DeterministicFailure(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(input.Pattern))
        {
            return PermissionTargetResolution.Reject(
                DeterministicFailure("The glob tool requires a pattern."));
        }

        try
        {
            _ = GlobPattern.Create(input.Pattern);
            var basePath = pathResolver.Resolve(context.WorkspaceRoot, input.BasePath);
            if (!Directory.Exists(basePath) && !File.Exists(basePath))
            {
                return PermissionTargetResolution.Reject(
                    DeterministicFailure("The glob base path does not exist."));
            }

            var limit = Math.Min(input.MaxResults ?? options.MaxGlobResults, options.MaxGlobResults);
            if (limit <= 0)
            {
                return PermissionTargetResolution.Reject(
                    DeterministicFailure("Glob maxResults must be greater than zero."));
            }

            var relativeBase = Path.GetRelativePath(context.WorkspaceRoot, basePath).Replace('\\', '/');
            var normalizedPattern = input.Pattern.Trim().Replace('\\', '/').TrimStart('/');
            var target = relativeBase == "."
                ? normalizedPattern
                : $"{relativeBase}/{normalizedPattern}";
            return PermissionTargetResolution.Success(
                "search",
                target,
                $"Search workspace files matching '{target}'.");
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

    private sealed record GlobArguments(string? Pattern, string? BasePath, int? MaxResults);
}
