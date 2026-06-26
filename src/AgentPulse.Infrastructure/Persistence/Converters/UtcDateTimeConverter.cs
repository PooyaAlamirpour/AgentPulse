using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentPulse.Infrastructure.Persistence.Converters;

internal sealed class UtcDateTimeConverter : ValueConverter<DateTime, long>
{
    public UtcDateTimeConverter()
        : base(
            value => value.Ticks,
            value => new DateTime(value, DateTimeKind.Utc))
    {
    }
}
