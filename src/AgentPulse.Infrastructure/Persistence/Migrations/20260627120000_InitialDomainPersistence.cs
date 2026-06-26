using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

public partial class InitialDomainPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                NormalizedRootPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                IsGitRepository = table.Column<bool>(type: "INTEGER", nullable: false),
                GitWorktree = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", project => project.Id);
            });

        migrationBuilder.CreateTable(
            name: "Sessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Sessions", session => session.Id);
                table.ForeignKey(
                    name: "FK_Sessions_Projects_ProjectId",
                    column: session => session.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Messages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Messages", message => message.Id);
                table.ForeignKey(
                    name: "FK_Messages_Sessions_SessionId",
                    column: message => message.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "MessageParts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                Order = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                PartType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Text = table.Column<string>(type: "TEXT", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MessageParts", part => part.Id);
                table.ForeignKey(
                    name: "FK_MessageParts_Messages_MessageId",
                    column: part => part.MessageId,
                    principalTable: "Messages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MessageParts_MessageId_Order",
            table: "MessageParts",
            columns: new[] { "MessageId", "Order" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Messages_SessionId_Sequence",
            table: "Messages",
            columns: new[] { "SessionId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Projects_NormalizedRootPath",
            table: "Projects",
            column: "NormalizedRootPath",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_ProjectId",
            table: "Sessions",
            column: "ProjectId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MessageParts");
        migrationBuilder.DropTable(name: "Messages");
        migrationBuilder.DropTable(name: "Sessions");
        migrationBuilder.DropTable(name: "Projects");
    }
}
