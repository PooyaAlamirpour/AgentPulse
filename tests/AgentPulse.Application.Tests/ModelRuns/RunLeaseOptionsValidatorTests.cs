using AgentPulse.Application.ModelRuns;
using AgentPulse.Application.SessionRuns;

namespace AgentPulse.Application.Tests.ModelRuns;

public sealed class RunLeaseOptionsValidatorTests
{
    [Theory]
    [InlineData(120, 30)]
    [InlineData(120, 60)]
    public void Accepts_renewal_at_or_below_half_of_duration(int durationSeconds, int renewalSeconds)
    {
        var session = new SessionRunOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(durationSeconds),
        };
        var streaming = new StreamingRunOptions
        {
            LeaseRenewInterval = TimeSpan.FromSeconds(renewalSeconds),
        };

        RunLeaseOptionsValidator.Validate(session, streaming);
    }

    [Theory]
    [InlineData(30, 45)]
    [InlineData(30, 30)]
    [InlineData(30, 16)]
    public void Rejects_renewal_above_half_of_duration(int durationSeconds, int renewalSeconds)
    {
        var session = new SessionRunOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(durationSeconds),
        };
        var streaming = new StreamingRunOptions
        {
            LeaseRenewInterval = TimeSpan.FromSeconds(renewalSeconds),
        };

        Assert.Throws<InvalidOperationException>(() =>
            RunLeaseOptionsValidator.Validate(session, streaming));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(30, 0)]
    [InlineData(30, -1)]
    [InlineData(5, 1)]
    [InlineData(30, 0.5)]
    public void Rejects_non_positive_or_excessively_small_values(
        double durationSeconds,
        double renewalSeconds)
    {
        var session = new SessionRunOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(durationSeconds),
        };
        var streaming = new StreamingRunOptions
        {
            LeaseRenewInterval = TimeSpan.FromSeconds(renewalSeconds),
        };

        Assert.Throws<InvalidOperationException>(() =>
            RunLeaseOptionsValidator.Validate(session, streaming));
    }
}
