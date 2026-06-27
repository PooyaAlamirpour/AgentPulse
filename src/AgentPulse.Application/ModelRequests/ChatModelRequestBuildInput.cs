using AgentPulse.Application.ProjectContexts;
using AgentPulse.Domain.Messages;

namespace AgentPulse.Application.ModelRequests;

public sealed record ChatModelRequestBuildInput(
    ProjectContext ProjectContext,
    IReadOnlyCollection<Message> OrderedPreviousHistory,
    Message CurrentUserMessage);
