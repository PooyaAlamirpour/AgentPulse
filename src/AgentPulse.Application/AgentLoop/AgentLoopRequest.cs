using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentLoop;

public sealed record AgentLoopRequest(
    IReadOnlyList<ChatModelMessage> Messages,
    string WorkspaceRoot,
    string? Model = null);
