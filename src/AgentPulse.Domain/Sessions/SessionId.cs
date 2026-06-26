namespace AgentPulse.Domain.Sessions;

public readonly record struct SessionId
{
    public SessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Session identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static SessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
