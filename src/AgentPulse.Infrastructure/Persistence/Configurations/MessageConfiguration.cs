using AgentPulse.Domain.Messages;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Id)
            .HasConversion<MessageIdConverter>()
            .ValueGeneratedNever();

        builder.Property(message => message.SessionId)
            .HasConversion<SessionIdConverter>()
            .IsRequired();

        builder.Property(message => message.Role)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(message => message.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(message => message.Sequence)
            .IsRequired();

        builder.Property(message => message.FailureReason)
            .HasMaxLength(1024);

        builder.HasIndex(message => new { message.SessionId, message.Sequence })
            .IsUnique();

        builder.Property(message => message.CreatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.Property(message => message.UpdatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.HasOne<AgentPulse.Domain.Sessions.Session>()
            .WithMany()
            .HasForeignKey(message => message.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(message => message.Parts)
            .WithOne()
            .HasForeignKey(part => part.MessageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(message => message.Parts)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
