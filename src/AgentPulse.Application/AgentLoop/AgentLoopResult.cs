using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentLoop;

public sealed record AgentLoopResult(
    string Text,
    ModelFinishReason FinishReason,
    ModelUsage? Usage,
    int Iterations);
