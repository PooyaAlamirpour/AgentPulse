using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

public partial class AddRunMessageMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Model",
            table: "Messages",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FinishReason",
            table: "Messages",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "InputTokens",
            table: "Messages",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "OutputTokens",
            table: "Messages",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "TotalTokens",
            table: "Messages",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FailureKind",
            table: "Messages",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FailureStage",
            table: "Messages",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "FailureStatusCode",
            table: "Messages",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Model", table: "Messages");
        migrationBuilder.DropColumn(name: "FinishReason", table: "Messages");
        migrationBuilder.DropColumn(name: "InputTokens", table: "Messages");
        migrationBuilder.DropColumn(name: "OutputTokens", table: "Messages");
        migrationBuilder.DropColumn(name: "TotalTokens", table: "Messages");
        migrationBuilder.DropColumn(name: "FailureKind", table: "Messages");
        migrationBuilder.DropColumn(name: "FailureStage", table: "Messages");
        migrationBuilder.DropColumn(name: "FailureStatusCode", table: "Messages");
    }
}
