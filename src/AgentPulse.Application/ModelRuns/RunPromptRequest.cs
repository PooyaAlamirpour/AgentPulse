using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.ModelRuns;

public sealed record RunPromptRequest(
    string Prompt,
    string? ProjectPath = null,
    SessionId? SessionId = null);
