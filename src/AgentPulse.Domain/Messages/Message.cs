using AgentPulse.Domain.Common;
using AgentPulse.Domain.Sessions;

namespace AgentPulse.Domain.Messages;

public sealed class Message
{
    private readonly List<MessagePart> _parts = [];

    private Message()
    {
    }

    public Message(
        MessageId id,
        SessionId sessionId,
        MessageRole role,
        long sequence,
        DateTime createdAtUtc)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Message identifier cannot be empty.", nameof(id));
        }

        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Session identifier cannot be empty.", nameof(sessionId));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role.");
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Message sequence must be greater than zero.");
        }

        Id = id;
        SessionId = sessionId;
        Role = role;
        Sequence = sequence;
        Status = MessageStatus.Pending;
        CreatedAtUtc = UtcDateTime.Ensure(createdAtUtc, nameof(createdAtUtc));
        UpdatedAtUtc = CreatedAtUtc;
    }

    public MessageId Id { get; private set; }

    public SessionId SessionId { get; private set; }

    public MessageRole Role { get; private set; }

    public MessageStatus Status { get; private set; }

    public long Sequence { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public IReadOnlyCollection<MessagePart> Parts => _parts.AsReadOnly();

    public TextMessagePart AddTextPart(
        MessagePartId id,
        int order,
        string text,
        DateTime createdAtUtc)
    {
        if (_parts.Any(part => part.Id == id))
        {
            throw new InvalidOperationException($"Message part '{id}' already exists.");
        }

        var expectedOrder = _parts.Count + 1;
        if (order != expectedOrder)
        {
            throw new InvalidOperationException(
                $"Message part order must be '{expectedOrder}', but was '{order}'.");
        }

        var part = new TextMessagePart(id, Id, order, text, createdAtUtc);
        _parts.Add(part);
        return part;
    }

    public void StartStreaming(DateTime updatedAtUtc)
    {
        TransitionTo(MessageStatus.Streaming, updatedAtUtc, MessageStatus.Pending);
    }

    public void Complete(DateTime updatedAtUtc)
    {
        TransitionTo(
            MessageStatus.Completed,
            updatedAtUtc,
            MessageStatus.Pending,
            MessageStatus.Streaming);
        FailureReason = null;
    }

    public void Fail(DateTime updatedAtUtc)
    {
        Fail(null, updatedAtUtc);
    }

    public void Fail(string? reason, DateTime updatedAtUtc)
    {
        var validatedReason = NormalizeFailureReason(reason);
        TransitionTo(
            MessageStatus.Failed,
            updatedAtUtc,
            MessageStatus.Pending,
            MessageStatus.Streaming);
        FailureReason = validatedReason;
    }

    public void Cancel(DateTime updatedAtUtc)
    {
        TransitionTo(
            MessageStatus.Cancelled,
            updatedAtUtc,
            MessageStatus.Pending,
            MessageStatus.Streaming);
        FailureReason = null;
    }

    private static string? NormalizeFailureReason(string? reason)
    {
        if (reason is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return reason.Trim();
    }

    private void TransitionTo(
        MessageStatus target,
        DateTime updatedAtUtc,
        params MessageStatus[] allowedCurrentStatuses)
    {
        if (!allowedCurrentStatuses.Contains(Status))
        {
            throw new InvalidOperationException(
                $"Message cannot transition from '{Status}' to '{target}'.");
        }

        var validatedUpdatedAtUtc = UtcDateTime.EnsureNotBefore(
            updatedAtUtc,
            CreatedAtUtc,
            nameof(updatedAtUtc));

        Status = target;
        UpdatedAtUtc = validatedUpdatedAtUtc;
    }
}
