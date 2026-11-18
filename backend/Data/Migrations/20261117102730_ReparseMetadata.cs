using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReparseMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FootnoteMarkers",
                table: "Documents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReReviewRequired",
                table: "Chunks",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FootnoteMarkers",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ReReviewRequired",
                table: "Chunks");
        }
    }
}
