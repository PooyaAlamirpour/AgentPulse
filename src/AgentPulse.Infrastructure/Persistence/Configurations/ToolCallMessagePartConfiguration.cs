using AgentPulse.Domain.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class ToolCallMessagePartConfiguration
    : IEntityTypeConfiguration<ToolCallMessagePart>
{
    public void Configure(EntityTypeBuilder<ToolCallMessagePart> builder)
    {
        builder.Property(part => part.ToolCallId)
            .HasColumnName("ToolCallId")
            .HasMaxLength(256);
        builder.Property(part => part.ToolName)
            .HasColumnName("ToolName")
            .HasMaxLength(128);
        builder.Property(part => part.ArgumentsJson);
    }
}
