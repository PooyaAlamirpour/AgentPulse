using AgentPulse.Domain.Sessions;

namespace AgentPulse.Cli.Commands;

public sealed record ParsedRunCommand(
    string? PositionalPrompt,
    string? ProjectDirectory,
    string? ModelOverride,
    SessionId? SessionId);
