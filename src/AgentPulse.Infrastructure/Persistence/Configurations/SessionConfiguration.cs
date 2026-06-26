using AgentPulse.Domain.Sessions;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Sessions");
        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id)
            .HasConversion<SessionIdConverter>()
            .ValueGeneratedNever();

        builder.Property(session => session.ProjectId)
            .HasConversion<ProjectIdConverter>()
            .IsRequired();

        builder.HasIndex(session => session.ProjectId);

        builder.Property(session => session.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(session => session.CreatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.Property(session => session.UpdatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.HasOne<AgentPulse.Domain.Projects.Project>()
            .WithMany()
            .HasForeignKey(session => session.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
