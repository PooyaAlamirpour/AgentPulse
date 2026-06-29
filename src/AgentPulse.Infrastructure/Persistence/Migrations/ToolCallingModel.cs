using AgentPulse.Domain.Messages;
using Microsoft.EntityFrameworkCore;

namespace AgentPulse.Infrastructure.Persistence.Migrations;

internal static class ToolCallingModel
{
    public static void Build(ModelBuilder modelBuilder)
    {
        RunMessageMetadataModel.Build(modelBuilder);

        modelBuilder.Entity<MessagePart>()
            .HasDiscriminator<string>("PartType")
            .HasValue<TextMessagePart>("text")
            .HasValue<ToolCallMessagePart>("tool_call")
            .HasValue<ToolResultMessagePart>("tool_result")
            .IsComplete(false);

        modelBuilder.Entity<ToolCallMessagePart>(builder =>
        {
            builder.HasBaseType<MessagePart>();
            builder.Property(part => part.ToolCallId)
                .HasColumnName("ToolCallId")
                .HasColumnType("TEXT")
                .HasMaxLength(256);
            builder.Property(part => part.ToolName)
                .HasColumnName("ToolName")
                .HasColumnType("TEXT")
                .HasMaxLength(128);
            builder.Property(part => part.ArgumentsJson)
                .HasColumnType("TEXT");
        });

        modelBuilder.Entity<ToolResultMessagePart>(builder =>
        {
            builder.HasBaseType<MessagePart>();
            builder.Property(part => part.ToolCallId)
                .HasColumnName("ToolCallId")
                .HasColumnType("TEXT")
                .HasMaxLength(256);
            builder.Property(part => part.ToolName)
                .HasColumnName("ToolName")
                .HasColumnType("TEXT")
                .HasMaxLength(128);
            builder.Property(part => part.Succeeded)
                .HasColumnType("INTEGER");
            builder.Property(part => part.Output)
                .HasColumnType("TEXT");
            builder.Property(part => part.Error)
                .HasColumnType("TEXT")
                .HasMaxLength(4096);
            builder.Property(part => part.MetadataJson)
                .HasColumnType("TEXT");
        });
    }
}
