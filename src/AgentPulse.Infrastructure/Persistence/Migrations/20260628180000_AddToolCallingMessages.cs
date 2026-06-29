using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

public partial class AddToolCallingMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ToolCallId",
            table: "MessageParts",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ToolName",
            table: "MessageParts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ArgumentsJson",
            table: "MessageParts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<bool>(
            name: "Succeeded",
            table: "MessageParts",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Output",
            table: "MessageParts",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "Error",
            table: "MessageParts",
            type: "TEXT",
            maxLength: 4096,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "MetadataJson",
            table: "MessageParts",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ToolCallId", table: "MessageParts");
        migrationBuilder.DropColumn(name: "ToolName", table: "MessageParts");
        migrationBuilder.DropColumn(name: "ArgumentsJson", table: "MessageParts");
        migrationBuilder.DropColumn(name: "Succeeded", table: "MessageParts");
        migrationBuilder.DropColumn(name: "Output", table: "MessageParts");
        migrationBuilder.DropColumn(name: "Error", table: "MessageParts");
        migrationBuilder.DropColumn(name: "MetadataJson", table: "MessageParts");
    }
}
