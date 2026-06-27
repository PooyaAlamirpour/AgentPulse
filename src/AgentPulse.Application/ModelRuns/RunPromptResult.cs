using AgentPulse.Application.ChatModels;
using AgentPulse.Domain.Messages;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.ModelRuns;

public sealed record RunPromptResult(
    SessionId SessionId,
    MessageId UserMessageId,
    MessageId AssistantMessageId,
    RunLeaseId RunLeaseId,
    string Text,
    ModelFinishReason FinishReason,
    ModelUsage? Usage,
    int FlushCount);
