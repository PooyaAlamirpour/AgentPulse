using System.Globalization;
using AgentPulse.Application.ProjectContexts;

namespace AgentPulse.Application.ModelRequests;

internal static class ProjectSystemContextFormatter
{
    private const string MissingValue = "(none)";

    public static string Format(ProjectContext projectContext)
    {
        return string.Join(
            '\n',
            "You are operating in the following project context:",
            string.Empty,
            $"Project ID: {projectContext.ProjectId}",
            $"Current directory: {projectContext.CurrentDirectory}",
            $"Project root: {projectContext.ProjectRoot}",
            $"Git repository: {FormatBoolean(projectContext.IsGitRepository)}",
            $"Git worktree: {projectContext.GitWorktree ?? MissingValue}",
            $"Platform: {FormatPlatform(projectContext.Platform)}",
            $"Current UTC date: {projectContext.CurrentUtcDate.ToString("O", CultureInfo.InvariantCulture)}");
    }

    private static string FormatBoolean(bool value) => value ? "true" : "false";

    private static string FormatPlatform(ProjectPlatform platform)
    {
        return platform switch
        {
            ProjectPlatform.Unknown => nameof(ProjectPlatform.Unknown),
            ProjectPlatform.Windows => nameof(ProjectPlatform.Windows),
            ProjectPlatform.Linux => nameof(ProjectPlatform.Linux),
            ProjectPlatform.MacOs => nameof(ProjectPlatform.MacOs),
            ProjectPlatform.FreeBsd => nameof(ProjectPlatform.FreeBsd),
            _ => throw new ChatModelRequestException(
                ChatModelRequestErrorCode.InvalidProjectContext,
                $"Project platform '{platform}' is not supported."),
        };
    }
}
