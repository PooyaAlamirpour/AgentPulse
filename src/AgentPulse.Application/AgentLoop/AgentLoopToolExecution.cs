using AgentPulse.Application.AgentTools;
using AgentPulse.Application.ChatModels;

namespace AgentPulse.Application.AgentLoop;

public sealed record AgentLoopToolExecution(
    ChatModelToolCall Call,
    AgentToolResult Result,
    TimeSpan Duration);
