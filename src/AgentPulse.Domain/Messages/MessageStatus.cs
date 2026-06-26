namespace AgentPulse.Domain.Messages;

public enum MessageStatus
{
    Pending = 0,
    Streaming = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}
