using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BinaryChunkEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDimensions",
                table: "Workspaces",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "Workspaces",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "EmbeddingModel",
                table: "Chunks",
                type: "nvarchar(max)",
                nullable: true,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingBatchSize",
                table: "Documents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingCompleted",
                table: "Documents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingTotal",
                table: "Documents",
                type: "int",
                nullable: true);

            // Renaming preserves every legacy JSON value verbatim. SQL Server's
            // nvarchar -> varbinary cast is UTF-16 bytes, NOT a float32 vector.
            migrationBuilder.RenameColumn(
                name: "Embedding",
                table: "Chunks",
                newName: "LegacyEmbeddingJson");

            migrationBuilder.AddColumn<byte[]>(
                name: "Embedding",
                table: "Chunks",
                type: "varbinary(max)",
                nullable: true);

            var reembeddingSql = """
                UPDATE [Chunks] SET [Status] = N'accepted' WHERE [Status] = N'embedded';

                UPDATE [Documents]
                SET [Status] = N'failed', [ProcessingOperation] = NULL, [FailureStage] = N'embedding',
                    [FailureReason] = N'Embedding was interrupted by the binary vector upgrade. Legacy vectors were archived. Retry Embed with the current model.',
                    [Revision] = [Revision] + 1, [UpdatedAt] = SYSUTCDATETIME()
                WHERE [ProcessingOperation] = N'embedding';

                UPDATE [Documents]
                SET [Status] = N'review', [Revision] = [Revision] + 1, [UpdatedAt] = SYSUTCDATETIME()
                WHERE [Status] = N'indexed';
                """;
            migrationBuilder.Sql("EXEC(N'" + reembeddingSql.Replace("'", "''") + "');");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Workspaces_EmbeddingDimensions",
                table: "Workspaces",
                sql: "[EmbeddingDimensions] IS NULL OR [EmbeddingDimensions] BETWEEN 1 AND 16384");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documents_EmbeddingBatchSize",
                table: "Documents",
                sql: "[EmbeddingBatchSize] IS NULL OR [EmbeddingBatchSize] BETWEEN 1 AND 32");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Documents_EmbeddingProgress",
                table: "Documents",
                sql: "[EmbeddingCompleted] >= 0 AND ([EmbeddingTotal] IS NULL OR ([EmbeddingTotal] >= 0 AND [EmbeddingCompleted] <= [EmbeddingTotal]))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // An automatic downgrade must not discard newly generated binary
            // vectors or misrepresent their bytes as JSON. Export/review first.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [Chunks] WHERE [Embedding] IS NOT NULL)
                    THROW 51000, N'Export binary embeddings before downgrading; no vectors were discarded.', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_Workspaces_EmbeddingDimensions",
                table: "Workspaces");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Documents_EmbeddingBatchSize",
                table: "Documents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Documents_EmbeddingProgress",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddingDimensions",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "Workspaces");

            migrationBuilder.AlterColumn<string>(
                name: "EmbeddingModel",
                table: "Chunks",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.DropColumn(
                name: "EmbeddingBatchSize",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddingCompleted",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddingTotal",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "Chunks");

            migrationBuilder.RenameColumn(
                name: "LegacyEmbeddingJson",
                table: "Chunks",
                newName: "Embedding");
        }
    }
}
