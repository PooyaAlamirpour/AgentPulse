using AgentPulse.Domain.Sessions;

namespace AgentPulse.Cli.Commands;

public sealed record RunCommandOptions(
    string Prompt,
    string? ProjectDirectory,
    string? ModelOverride,
    SessionId? SessionId);
