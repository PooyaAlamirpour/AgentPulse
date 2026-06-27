using AgentPulse.Domain.SessionRuns;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class RunLeaseConfiguration : IEntityTypeConfiguration<RunLease>
{
    public void Configure(EntityTypeBuilder<RunLease> builder)
    {
        builder.ToTable("RunLeases");
        builder.HasKey(runLease => runLease.SessionId);

        builder.Property(runLease => runLease.SessionId)
            .HasConversion<SessionIdConverter>()
            .ValueGeneratedNever();

        builder.Property(runLease => runLease.LeaseId)
            .HasConversion<RunLeaseIdConverter>()
            .IsRequired();

        builder.HasIndex(runLease => runLease.LeaseId)
            .IsUnique();

        builder.Property(runLease => runLease.AcquiredAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.Property(runLease => runLease.ExpiresAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.HasOne<AgentPulse.Domain.Sessions.Session>()
            .WithOne()
            .HasForeignKey<RunLease>(runLease => runLease.SessionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
