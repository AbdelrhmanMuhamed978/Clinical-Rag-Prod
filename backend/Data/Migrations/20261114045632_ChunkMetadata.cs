using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChunkMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chunks_DocumentId",
                table: "Chunks");

            migrationBuilder.AddColumn<string>(
                name: "GuidelineStatus",
                table: "Documents",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Society",
                table: "Documents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedBy",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "Documents",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SectionHierarchy",
                table: "Chunks",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "Chunks",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Chunks",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ValidationError",
                table: "Chunks",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Verbatim",
                table: "Chunks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_DocumentId_Page",
                table: "Chunks",
                columns: new[] { "DocumentId", "Page" });

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_WorkspaceId_Status",
                table: "Chunks",
                columns: new[] { "WorkspaceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chunks_DocumentId_Page",
                table: "Chunks");

            migrationBuilder.DropIndex(
                name: "IX_Chunks_WorkspaceId_Status",
                table: "Chunks");

            migrationBuilder.DropColumn(
                name: "GuidelineStatus",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Society",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "Chunks");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Chunks");

            migrationBuilder.DropColumn(
                name: "ValidationError",
                table: "Chunks");

            migrationBuilder.DropColumn(
                name: "Verbatim",
                table: "Chunks");

            migrationBuilder.AlterColumn<string>(
                name: "SectionHierarchy",
                table: "Chunks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chunks_DocumentId",
                table: "Chunks",
                column: "DocumentId");
        }
    }
}
