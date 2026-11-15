using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ColabOperationalConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColabConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ManualOverride = table.Column<bool>(type: "bit", nullable: false),
                    InstanceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColabConnections", x => x.Id);
                    table.CheckConstraint("CK_ColabConnections_Revision", "[Revision] >= 1");
                    table.CheckConstraint("CK_ColabConnections_Singleton", "[Id] = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently discard the active protected connection during an application rollback.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [ColabConnections])
                    THROW 51001, 'Export and explicitly remove the operational Colab connection before downgrading this migration.', 1;
                """);
            migrationBuilder.DropTable(
                name: "ColabConnections");
        }
    }
}
