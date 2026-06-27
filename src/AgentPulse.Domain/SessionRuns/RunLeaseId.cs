namespace AgentPulse.Domain.SessionRuns;

public readonly record struct RunLeaseId
{
    public RunLeaseId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Run lease identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static RunLeaseId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
