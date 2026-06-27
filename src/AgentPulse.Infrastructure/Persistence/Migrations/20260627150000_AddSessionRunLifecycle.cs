using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

public partial class AddSessionRunLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FailureReason",
            table: "Messages",
            type: "TEXT",
            maxLength: 1024,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "RunLeases",
            columns: table => new
            {
                SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                LeaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                AcquiredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunLeases", runLease => runLease.SessionId);
                table.ForeignKey(
                    name: "FK_RunLeases_Sessions_SessionId",
                    column: runLease => runLease.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RunLeases_LeaseId",
            table: "RunLeases",
            column: "LeaseId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RunLeases");

        migrationBuilder.DropColumn(
            name: "FailureReason",
            table: "Messages");
    }
}
