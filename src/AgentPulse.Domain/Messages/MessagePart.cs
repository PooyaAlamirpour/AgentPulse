using AgentPulse.Domain.Common;

namespace AgentPulse.Domain.Messages;

public abstract class MessagePart
{
    protected MessagePart()
    {
    }

    protected MessagePart(
        MessagePartId id,
        MessageId messageId,
        int order,
        DateTime createdAtUtc)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Message part identifier cannot be empty.", nameof(id));
        }

        if (messageId.Value == Guid.Empty)
        {
            throw new ArgumentException("Message identifier cannot be empty.", nameof(messageId));
        }

        if (order <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(order),
                order,
                "Message part order must be greater than zero.");
        }

        Id = id;
        MessageId = messageId;
        Order = order;
        CreatedAtUtc = UtcDateTime.Ensure(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public MessagePartId Id { get; private set; }

    public MessageId MessageId { get; private set; }

    public int Order { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; protected set; }

    protected DateTime EnsureUpdateTimestamp(DateTime updatedAtUtc)
    {
        return UtcDateTime.EnsureNotBefore(
            updatedAtUtc,
            CreatedAtUtc,
            nameof(updatedAtUtc));
    }
}
