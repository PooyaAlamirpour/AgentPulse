using AgentPulse.Domain.Messages;

namespace AgentPulse.Application.ModelRequests;

public sealed class ChatModelHistoryPolicy : IChatModelHistoryPolicy
{
    public bool ShouldInclude(MessageStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ChatModelRequestException(
                ChatModelRequestErrorCode.UnsupportedMessageStatus,
                $"Message status '{status}' is not supported.");
        }

        return status == MessageStatus.Completed;
    }
}
