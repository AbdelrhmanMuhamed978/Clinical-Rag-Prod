using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentProcessingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "Documents",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureStage",
                table: "Documents",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessingOperation",
                table: "Documents",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Documents",
                type: "int",
                nullable: false,
                defaultValue: 1);

            // The old API could leave an idle document "in_progress" forever or
            // manufacture "indexed" via PATCH. Preserve checkpoints, never invent
            // successful completion when upgrading those rows.
            var processingBackfillSql = """
                UPDATE [Documents]
                SET [FailureStage] = CASE WHEN [Status] = 'parsing' THEN 'parsing' ELSE 'chunking' END,
                    [FailureReason] = CASE WHEN [Status] = 'parsing'
                        THEN N'Previous parsing was interrupted. Retry Parse, or use the retained parsed text if available.'
                        ELSE N'Previous chunking was interrupted. Completed pages were kept. Choose the remaining pages to retry.' END,
                    [ChunkingStatus] = CASE WHEN [Status] = 'parsing' THEN [ChunkingStatus] ELSE 'failed' END,
                    [Status] = 'failed'
                WHERE [Status] IN ('parsing', 'chunking') OR [ChunkingStatus] = 'in_progress';

                UPDATE [Documents]
                SET [FailureStage] = CASE WHEN NULLIF(LTRIM(RTRIM([ParsedText])), '') IS NULL THEN 'parsing' ELSE 'chunking' END,
                    [FailureReason] = N'Previous processing failed; no reason was recorded. Retry the failed step or upload the PDF again.'
                WHERE [Status] = 'failed' AND [FailureReason] IS NULL;

                UPDATE d SET [Status] = 'review'
                FROM [Documents] d
                WHERE d.[Status] = 'indexed' AND (
                    NOT EXISTS (SELECT 1 FROM [Chunks] c WHERE c.[DocumentId] = d.[Id] AND c.[WorkspaceId] = d.[WorkspaceId]
                        AND c.[Status] = 'embedded' AND NULLIF(LTRIM(RTRIM(c.[Embedding])), '') IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM [Chunks] c WHERE c.[DocumentId] = d.[Id] AND c.[WorkspaceId] = d.[WorkspaceId]
                        AND (c.[Status] NOT IN ('rejected', 'embedded') OR (c.[Status] = 'embedded' AND NULLIF(LTRIM(RTRIM(c.[Embedding])), '') IS NULL)))
                );
                """;
            // Idempotent scripts share a batch with ADD COLUMN. Compile this only
            // after those columns exist, as with the Rules review backfill.
            migrationBuilder.Sql("EXEC(N'" + processingBackfillSql.Replace("'", "''") + "');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "FailureStage",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ProcessingOperation",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Documents");
        }
    }
}
