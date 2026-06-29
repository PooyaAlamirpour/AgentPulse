using AgentPulse.Domain.Messages;
using AgentPulse.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence.Migrations;

internal static class ToolResultTurnIntegrityModel
{
    public static void Build(ModelBuilder modelBuilder)
    {
        ToolCallingModel.Build(modelBuilder);

        modelBuilder.Entity<ToolResultMessagePart>(builder =>
        {
            builder.Property(part => part.SessionId)
                .HasColumnName("ToolResultSessionId")
                .HasColumnType("TEXT")
                .HasConversion<SessionIdConverter>()
                .IsRequired();
            builder.Property(part => part.AssistantToolCallMessageId)
                .HasColumnName("AssistantToolCallMessageId")
                .HasColumnType("TEXT")
                .HasConversion<MessageIdConverter>()
                .IsRequired();
            builder.HasIndex(part => new
                {
                    part.SessionId,
                    part.AssistantToolCallMessageId,
                    part.ToolCallId,
                })
                .IsUnique()
                .HasFilter("PartType = 'tool_result' AND ToolResultSessionId IS NOT NULL AND AssistantToolCallMessageId IS NOT NULL AND ToolCallId IS NOT NULL")
                .HasDatabaseName("UX_MessageParts_ToolResult_Session_Turn_Call");
        });
    }
}
