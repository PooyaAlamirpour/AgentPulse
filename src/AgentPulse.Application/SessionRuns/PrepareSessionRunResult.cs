using AgentPulse.Domain.Messages;
using AgentPulse.Domain.Projects;
using AgentPulse.Domain.SessionRuns;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Application.SessionRuns;

public sealed record PrepareSessionRunResult(
    Project Project,
    Session Session,
    Message UserMessage,
    Message AssistantMessage,
    IReadOnlyList<Message> OrderedPreviousHistory,
    RunLease RunLease);
