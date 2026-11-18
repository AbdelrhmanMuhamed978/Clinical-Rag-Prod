using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class NeedsOcrPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NeedsOcrPages",
                table: "Documents",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NeedsOcrPages",
                table: "Documents");
        }
    }
}
