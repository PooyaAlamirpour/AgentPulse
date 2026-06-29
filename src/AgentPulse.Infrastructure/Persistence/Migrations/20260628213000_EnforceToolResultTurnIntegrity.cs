using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentPulse.Infrastructure.Persistence.Migrations;

public partial class EnforceToolResultTurnIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "AssistantToolCallMessageId",
            table: "MessageParts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ToolResultSessionId",
            table: "MessageParts",
            type: "TEXT",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE MessageParts AS result
            SET ToolResultSessionId = (
                    SELECT toolMessage.SessionId
                    FROM Messages AS toolMessage
                    WHERE toolMessage.Id = result.MessageId),
                AssistantToolCallMessageId = (
                    SELECT assistant.Id
                    FROM Messages AS toolMessage
                    JOIN Messages AS assistant
                      ON assistant.SessionId = toolMessage.SessionId
                     AND assistant.Role = 'Assistant'
                     AND assistant.Sequence < toolMessage.Sequence
                    JOIN MessageParts AS call
                      ON call.MessageId = assistant.Id
                     AND call.PartType = 'tool_call'
                     AND call.ToolCallId = result.ToolCallId
                     AND call.ToolName = result.ToolName
                    WHERE toolMessage.Id = result.MessageId
                    ORDER BY assistant.Sequence DESC
                    LIMIT 1)
            WHERE result.PartType = 'tool_result';
            """);

        migrationBuilder.Sql(
            """
            DELETE FROM MessageParts AS duplicate
            WHERE duplicate.PartType = 'tool_result'
              AND duplicate.ToolResultSessionId IS NOT NULL
              AND duplicate.AssistantToolCallMessageId IS NOT NULL
              AND duplicate.ToolCallId IS NOT NULL
              AND EXISTS (
                  SELECT 1
                  FROM MessageParts AS kept
                  WHERE kept.PartType = 'tool_result'
                    AND kept.ToolResultSessionId IS duplicate.ToolResultSessionId
                    AND kept.AssistantToolCallMessageId IS duplicate.AssistantToolCallMessageId
                    AND kept.ToolCallId IS duplicate.ToolCallId
                    AND kept.ToolName IS duplicate.ToolName
                    AND kept.Succeeded IS duplicate.Succeeded
                    AND kept.Output IS duplicate.Output
                    AND kept.Error IS duplicate.Error
                    AND kept.MetadataJson IS duplicate.MetadataJson
                    AND kept.Id < duplicate.Id);
            """);

        migrationBuilder.Sql(
            """
            CREATE TEMP TABLE __ToolResultIntegrityConflictGuard (
                HasConflict INTEGER NOT NULL);

            CREATE TEMP TRIGGER __ToolResultIntegrityConflictGuard_Fail
            BEFORE INSERT ON __ToolResultIntegrityConflictGuard
            WHEN NEW.HasConflict = 1
            BEGIN
                SELECT RAISE(
                    ABORT,
                    'Conflicting tool results exist for the same session, assistant turn, and tool call. Resolve them before applying migration.');
            END;

            INSERT INTO __ToolResultIntegrityConflictGuard (HasConflict)
            SELECT 1
            WHERE EXISTS (
                SELECT 1
                FROM MessageParts
                WHERE PartType = 'tool_result'
                  AND ToolResultSessionId IS NOT NULL
                  AND AssistantToolCallMessageId IS NOT NULL
                  AND ToolCallId IS NOT NULL
                GROUP BY ToolResultSessionId, AssistantToolCallMessageId, ToolCallId
                HAVING COUNT(*) > 1);

            DROP TRIGGER __ToolResultIntegrityConflictGuard_Fail;
            DROP TABLE __ToolResultIntegrityConflictGuard;
            """);

        migrationBuilder.CreateIndex(
            name: "UX_MessageParts_ToolResult_Session_Turn_Call",
            table: "MessageParts",
            columns: new[] { "ToolResultSessionId", "AssistantToolCallMessageId", "ToolCallId" },
            unique: true,
            filter: "PartType = 'tool_result' AND ToolResultSessionId IS NOT NULL AND AssistantToolCallMessageId IS NOT NULL AND ToolCallId IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_MessageParts_ToolResult_Session_Turn_Call",
            table: "MessageParts");

        migrationBuilder.DropColumn(
            name: "AssistantToolCallMessageId",
            table: "MessageParts");

        migrationBuilder.DropColumn(
            name: "ToolResultSessionId",
            table: "MessageParts");
    }
}
