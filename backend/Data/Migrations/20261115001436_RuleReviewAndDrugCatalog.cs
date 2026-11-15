using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RuleReviewAndDrugCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "Rules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "Rules",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ReviewedCatalogRevision",
                table: "Rules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Rules",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Rules",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "draft");

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                table: "Rules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DrugCatalogs",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CatalogJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrugCatalogs", x => x.WorkspaceId);
                    table.CheckConstraint("CK_DrugCatalogs_Revision", "[Revision] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "RuleAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RuleKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_WorkspaceId_RuleId",
                table: "Rules",
                columns: new[] { "WorkspaceId", "RuleId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Rules_Revision",
                table: "Rules",
                sql: "[Revision] >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Rules_Status",
                table: "Rules",
                sql: "[Status] IN ('draft', 'in_review', 'approved', 'retired')");

            migrationBuilder.CreateIndex(
                name: "IX_RuleAuditEntries_WorkspaceId_RuleId_CreatedAt",
                table: "RuleAuditEntries",
                columns: new[] { "WorkspaceId", "RuleId", "CreatedAt" });

            // Old reviews were not bound to a validated definition and recognition catalog.
            // Preserve their evidence, but never carry their implicit activation into this lifecycle.
            var legacyReviewResetSql = """
                DECLARE @legacyReviewResetAt datetime2 = SYSUTCDATETIME();
                INSERT INTO [RuleAuditEntries]
                    ([Id], [WorkspaceId], [RuleId], [RuleKey], [Action], [ActorId], [BeforeJson], [AfterJson], [CreatedAt])
                SELECT NEWID(), r.[WorkspaceId], r.[Id], r.[RuleId], N'legacy_review_reset', NULL,
                    (SELECT r.[Id] AS [id], r.[WorkspaceId] AS [workspaceId], r.[RuleId] AS [ruleId],
                        r.[Title] AS [title], r.[DrugClasses] AS [drugClasses], r.[AppliesWhen] AS [appliesWhen],
                        r.[Level] AS [level], r.[Message] AS [message], r.[Caveat] AS [caveat],
                        r.[Sources] AS [sources], r.[LinkTerms] AS [linkTerms], r.[CaseLevel] AS [caseLevel],
                        r.[ReviewedBy] AS [reviewedBy], r.[ReviewedAt] AS [reviewedAt],
                        r.[CreatedAt] AS [createdAt], r.[UpdatedAt] AS [updatedAt]
                     FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER),
                    (SELECT r.[Id] AS [id], r.[WorkspaceId] AS [workspaceId], r.[RuleId] AS [ruleId],
                        r.[Title] AS [title], r.[DrugClasses] AS [drugClasses], r.[AppliesWhen] AS [appliesWhen],
                        r.[Level] AS [level], r.[Message] AS [message], r.[Caveat] AS [caveat],
                        r.[Sources] AS [sources], r.[LinkTerms] AS [linkTerms], r.[CaseLevel] AS [caseLevel],
                        N'draft' AS [status], CAST(0 AS bit) AS [enabled], 1 AS [revision],
                        CAST(NULL AS uniqueidentifier) AS [reviewedBy], CAST(NULL AS datetime2) AS [reviewedAt],
                        CAST(NULL AS int) AS [reviewedCatalogRevision],
                        CAST(NULL AS uniqueidentifier) AS [createdBy], CAST(NULL AS uniqueidentifier) AS [updatedBy],
                        r.[CreatedAt] AS [createdAt], @legacyReviewResetAt AS [updatedAt]
                     FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER),
                    @legacyReviewResetAt
                FROM [Rules] r;

                UPDATE [Rules] SET [Status] = N'draft', [Enabled] = 0, [Revision] = 1,
                    [ReviewedBy] = NULL, [ReviewedAt] = NULL, [ReviewedCatalogRevision] = NULL,
                    [UpdatedAt] = @legacyReviewResetAt;
                """;
            // In an idempotent script this shares a batch with ADD COLUMN. Defer compilation
            // until the new columns exist; the migration-history guard also makes repeats safe.
            migrationBuilder.Sql("EXEC(N'" + legacyReviewResetSql.Replace("'", "''") + "');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A schema downgrade is lossy (catalogs and audit history). It must not restore
            // legacy activation; export/backup those tables before a deliberate downgrade.
            migrationBuilder.DropTable(
                name: "DrugCatalogs");

            migrationBuilder.DropTable(
                name: "RuleAuditEntries");

            migrationBuilder.DropIndex(
                name: "IX_Rules_WorkspaceId_RuleId",
                table: "Rules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Rules_Revision",
                table: "Rules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Rules_Status",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "ReviewedCatalogRevision",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "Rules");
        }
    }
}
