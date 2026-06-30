using AgentPulse.Application.ChatModels;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.AgentLoop;

public sealed record AgentLoopRequest(
    IReadOnlyList<ChatModelMessage> Messages,
    string WorkspaceRoot,
    string? Model = null,
    SessionId? SessionId = null,
    ProjectId? ProjectId = null);
