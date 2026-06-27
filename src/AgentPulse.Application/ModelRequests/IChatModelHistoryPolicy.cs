using AgentPulse.Domain.Messages;

namespace AgentPulse.Application.ModelRequests;

public interface IChatModelHistoryPolicy
{
    bool ShouldInclude(MessageStatus status);
}
