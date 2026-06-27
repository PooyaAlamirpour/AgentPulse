using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public sealed record PrepareSessionRunRequest(
    ProjectContext ProjectContext,
    SessionId? SessionId,
    string UserPrompt);
