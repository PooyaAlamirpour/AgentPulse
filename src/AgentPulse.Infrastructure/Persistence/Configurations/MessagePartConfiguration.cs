using AgentPulse.Domain.Messages;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class MessagePartConfiguration : IEntityTypeConfiguration<MessagePart>
{
    public void Configure(EntityTypeBuilder<MessagePart> builder)
    {
        builder.ToTable("MessageParts");
        builder.HasKey(part => part.Id);

        builder.Property(part => part.Id)
            .HasConversion<MessagePartIdConverter>()
            .ValueGeneratedNever();

        builder.Property(part => part.MessageId)
            .HasConversion<MessageIdConverter>()
            .IsRequired();

        builder.Property(part => part.Order)
            .IsRequired();

        builder.HasIndex(part => new { part.MessageId, part.Order })
            .IsUnique();

        builder.Property(part => part.CreatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.Property(part => part.UpdatedAtUtc)
            .HasConversion<UtcDateTimeConverter>()
            .IsRequired();

        builder.Property<string>("PartType")
            .HasMaxLength(32)
            .IsRequired();

        builder.HasDiscriminator<string>("PartType")
            .HasValue<TextMessagePart>("text")
            .HasValue<ToolCallMessagePart>("tool_call")
            .HasValue<ToolResultMessagePart>("tool_result")
            .IsComplete(false);
    }
}
