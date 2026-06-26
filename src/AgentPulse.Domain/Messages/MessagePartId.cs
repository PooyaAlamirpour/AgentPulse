namespace AgentPulse.Domain.Messages;

public readonly record struct MessagePartId
{
    public MessagePartId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Message part identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static MessagePartId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
