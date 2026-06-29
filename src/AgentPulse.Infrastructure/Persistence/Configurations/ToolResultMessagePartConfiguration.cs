using AgentPulse.Domain.Messages;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPulse.Infrastructure.Persistence.Configurations;

internal sealed class ToolResultMessagePartConfiguration
    : IEntityTypeConfiguration<ToolResultMessagePart>
{
    public void Configure(EntityTypeBuilder<ToolResultMessagePart> builder)
    {
        builder.Property(part => part.SessionId)
            .HasColumnName("ToolResultSessionId")
            .HasConversion<SessionIdConverter>()
            .IsRequired();
        builder.Property(part => part.AssistantToolCallMessageId)
            .HasColumnName("AssistantToolCallMessageId")
            .HasConversion<MessageIdConverter>()
            .IsRequired();
        builder.Property(part => part.ToolCallId)
            .HasColumnName("ToolCallId")
            .HasMaxLength(256);
        builder.Property(part => part.ToolName)
            .HasColumnName("ToolName")
            .HasMaxLength(128);
        builder.Property(part => part.Succeeded);
        builder.Property(part => part.Output);
        builder.Property(part => part.Error).HasMaxLength(4096);
        builder.Property(part => part.MetadataJson);

        builder.HasIndex(part => new
            {
                part.SessionId,
                part.AssistantToolCallMessageId,
                part.ToolCallId,
            })
            .IsUnique()
            .HasFilter("PartType = 'tool_result' AND ToolResultSessionId IS NOT NULL AND AssistantToolCallMessageId IS NOT NULL AND ToolCallId IS NOT NULL")
            .HasDatabaseName("UX_MessageParts_ToolResult_Session_Turn_Call");
    }
}
