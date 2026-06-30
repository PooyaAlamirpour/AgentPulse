using System.Text;
using System.Text.Json;
using AgentPulse.Application.AgentTools;
using AgentPulse.Application.Workspaces;
using AgentPulse.Infrastructure.Mutations;

namespace AgentPulse.Infrastructure.AgentTools;

public sealed class ReadAgentTool(
    IWorkspacePathResolver pathResolver,
    AgentToolOptions options) : IAgentTool, IPermissionAwareAgentTool
{
    public string Name => "read";

    public string Description =>
        "Read a text file inside the active workspace with optional one-based line offset and line limit.";

    public string ParametersJsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"integer","minimum":1},"limit":{"type":"integer","minimum":1}},"required":["path"],"additionalProperties":false}
        """;

    public async Task<AgentToolResult> ExecuteAsync(
        JsonElement arguments,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        options.Validate();

        ReadArguments input;
        try
        {
            input = ToolArgumentReader.Deserialize<ReadArguments>(arguments);
        }
        catch (ArgumentException exception)
        {
            return DeterministicFailure(exception.Message);
        }

        if (string.IsNullOrWhiteSpace(input.Path))
        {
            return DeterministicFailure("The read tool requires a non-empty path.");
        }

        var offset = input.Offset ?? 1;
        var requestedLimit = input.Limit ?? options.MaxReadLines;
        if (offset <= 0 || requestedLimit <= 0)
        {
            return DeterministicFailure("Read offset and limit must be greater than zero.");
        }

        var limit = Math.Min(requestedLimit, options.MaxReadLines);
        string path;
        try
        {
            path = pathResolver.Resolve(context.WorkspaceRoot, input.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return DeterministicFailure(exception.Message);
        }

        if (!File.Exists(path))
        {
            return Directory.Exists(path)
                ? DeterministicFailure("The requested read path is a directory, not a file.")
                : DeterministicFailure("The requested file does not exist.");
        }

        var fileLength = new FileInfo(path).Length;
        if (fileLength > options.MaxReadableFileBytes)
        {
            return DeterministicFailure(
                $"The requested file is {fileLength} bytes and exceeds the maximum readable size of {options.MaxReadableFileBytes} bytes.");
        }

        var exactBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (exactBytes.LongLength > options.MaxReadableFileBytes)
        {
            return DeterministicFailure(
                $"The requested file changed while it was being read and now exceeds the maximum readable size of {options.MaxReadableFileBytes} bytes.");
        }

        var builder = new StringBuilder(Math.Min(options.MaxOutputCharacters, 4096));
        var lineNumber = 0;
        var emitted = 0;
        var truncated = false;

        using var stream = new MemoryStream(exactBytes, writable: false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: true);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;

            if (lineNumber < offset)
            {
                continue;
            }

            if (emitted >= limit)
            {
                truncated = true;
                break;
            }

            var rendered = $"{lineNumber}: {line}{Environment.NewLine}";
            var remaining = options.MaxOutputCharacters - builder.Length;
            if (rendered.Length > remaining)
            {
                if (remaining > 0)
                {
                    builder.Append(rendered.AsSpan(0, remaining));
                }

                truncated = true;
                break;
            }

            builder.Append(rendered);
            emitted++;
        }

        if (truncated)
        {
            const string notice = "\n[Read output truncated. Use a larger offset or a smaller limit to continue.]";
            if (builder.Length + notice.Length <= options.MaxOutputCharacters)
            {
                builder.Append(notice);
            }
        }

        var relative = Path.GetRelativePath(context.WorkspaceRoot, path).Replace('\\', '/');
        return AgentToolResult.Success(
            builder.ToString().TrimEnd(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = relative,
                ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lines"] = emitted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["truncated"] = truncated.ToString().ToLowerInvariant(),
                ["sha256"] = TextFileCodec.ComputeSha256(exactBytes),
                ["encoding"] = TextFileCodec.DescribeEncoding(exactBytes),
                ["lineEnding"] = TextFileCodec.DescribeLineEnding(exactBytes),
            });
    }

    public PermissionTargetResolution ResolvePermissionTarget(
        JsonElement arguments,
        AgentToolExecutionContext context)
    {
        ReadArguments input;
        try
        {
            input = ToolArgumentReader.Deserialize<ReadArguments>(arguments);
        }
        catch (ArgumentException exception)
        {
            return PermissionTargetResolution.Reject(DeterministicFailure(exception.Message));
        }

        if (string.IsNullOrWhiteSpace(input.Path))
        {
            return PermissionTargetResolution.Reject(
                DeterministicFailure("The read tool requires a non-empty path."));
        }

        var offset = input.Offset ?? 1;
        var requestedLimit = input.Limit ?? options.MaxReadLines;
        if (offset <= 0 || requestedLimit <= 0)
        {
            return PermissionTargetResolution.Reject(
                DeterministicFailure("Read offset and limit must be greater than zero."));
        }

        try
        {
            var path = pathResolver.Resolve(context.WorkspaceRoot, input.Path);
            if (!File.Exists(path))
            {
                return PermissionTargetResolution.Reject(
                    Directory.Exists(path)
                        ? DeterministicFailure("The requested read path is a directory, not a file.")
                        : DeterministicFailure("The requested file does not exist."));
            }

            var relative = Path.GetRelativePath(context.WorkspaceRoot, path).Replace('\\', '/');
            return PermissionTargetResolution.Success(
                "read",
                relative,
                $"Read workspace file '{relative}'.");
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

    private sealed record ReadArguments(string? Path, int? Offset, int? Limit);
}
