using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchGenerationCoherence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SearchGeneration",
                table: "Workspaces",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Workspaces_SearchGeneration",
                table: "Workspaces",
                sql: "[SearchGeneration] >= 0");

            // Every chunk writer, including another API instance or direct SQL,
            // advances the same durable generation in the chunk transaction.
            // Dynamic SQL keeps CREATE/ALTER TRIGGER legal inside the idempotent
            // script's IF block; SQL Server requires it to start its own batch.
            migrationBuilder.Sql("""
                EXEC(N'CREATE OR ALTER TRIGGER [dbo].[TR_Chunks_SearchGeneration]
                ON [dbo].[Chunks]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    ;WITH changed AS (
                        SELECT [WorkspaceId] FROM inserted
                        UNION
                        SELECT [WorkspaceId] FROM deleted
                    )
                    UPDATE target
                    SET [SearchGeneration] = [SearchGeneration] + 1
                    FROM [dbo].[Workspaces] AS target
                    INNER JOIN changed ON changed.[WorkspaceId] = target.[Id];
                END;');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_Chunks_SearchGeneration];");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Workspaces_SearchGeneration",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SearchGeneration",
                table: "Workspaces");
        }
    }
}
