using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Workspaces;

namespace AgentPulse.Infrastructure.AgentTools;

public sealed class GlobAgentTool(
    IWorkspacePathResolver pathResolver,
    AgentToolOptions options) : IAgentTool
{
    public string Name => "glob";

    public string Description =>
        "Find files inside the active workspace using a cross-platform glob pattern such as **/*.cs.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"pattern":{"type":"string"},"basePath":{"type":"string"},"maxResults":{"type":"integer","minimum":1}},"required":["pattern"],"additionalProperties":false}
        """;

    public Task<AgentToolResult> ExecuteAsync(
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
            return Task.FromResult(AgentToolResult.Failure(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(input.Pattern))
        {
            return Task.FromResult(AgentToolResult.Failure("The glob tool requires a pattern."));
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
            return Task.FromResult(AgentToolResult.Failure(exception.Message));
        }

        if (!Directory.Exists(basePath) && !File.Exists(basePath))
        {
            return Task.FromResult(AgentToolResult.Failure("The glob base path does not exist."));
        }

        var limit = Math.Min(input.MaxResults ?? options.MaxGlobResults, options.MaxGlobResults);
        if (limit <= 0)
        {
            return Task.FromResult(AgentToolResult.Failure("Glob maxResults must be greater than zero."));
        }

        var matches = WorkspaceFileEnumerator
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

        var output = matches.Length == 0
            ? "No files found."
            : string.Join("\n", matches.Take(limit).Select(static value => value.RelativeToWorkspace));
        var truncated = matches.Length > limit;
        if (truncated)
        {
            output += $"\n\n[Results truncated: showing {limit} of {matches.Length} files.]";
        }

        return Task.FromResult(AgentToolResult.Success(
            output,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["matches"] = matches.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["truncated"] = truncated.ToString().ToLowerInvariant(),
            }));
    }

    private sealed record GlobArguments(string? Pattern, string? BasePath, int? MaxResults);
}
