using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GovernanceRegistryFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowSupersededGuidelines",
                table: "Workspaces",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedSourceUrl",
                table: "Rules",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewerName",
                table: "Rules",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveDate",
                table: "Documents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermissionNote",
                table: "Documents",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewByDate",
                table: "Documents",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "Documents",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Documents",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Version",
                table: "Documents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnswerFlags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigVersion = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnswerFlags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnswerFlags_AnalysisRunId",
                table: "AnswerFlags",
                column: "AnalysisRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AnswerFlags_WorkspaceId_CreatedAt",
                table: "AnswerFlags",
                columns: new[] { "WorkspaceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnswerFlags");

            migrationBuilder.DropColumn(
                name: "AllowSupersededGuidelines",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "ApprovedSourceUrl",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "ReviewerName",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "EffectiveDate",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "PermissionNote",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ReviewByDate",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Documents");
        }
    }
}
