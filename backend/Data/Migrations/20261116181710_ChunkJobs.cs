using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChunkJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChunkJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CompletedPagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FailedPagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SkippedPagesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Guidance = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    BatchSize = table.Column<int>(type: "int", nullable: false),
                    MaxTokens = table.Column<int>(type: "int", nullable: false),
                    BatchTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    MaxRetries = table.Column<int>(type: "int", nullable: false),
                    FailureMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TotalBatches = table.Column<int>(type: "int", nullable: false),
                    CompletedBatches = table.Column<int>(type: "int", nullable: false),
                    CurrentBatchStart = table.Column<int>(type: "int", nullable: true),
                    CurrentBatchEnd = table.Column<int>(type: "int", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PauseRequested = table.Column<bool>(type: "bit", nullable: false),
                    CancelRequested = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RetryOfJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastHeartbeatAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChunkJobs", x => x.Id);
                    table.CheckConstraint("CK_ChunkJobs_BatchSize", "[BatchSize] BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_ChunkJobs_FailureMode", "[FailureMode] IN ('continue', 'stop')");
                    table.CheckConstraint("CK_ChunkJobs_MaxTokens", "[MaxTokens] BETWEEN 100 AND 2000");
                    table.CheckConstraint("CK_ChunkJobs_Retries", "[MaxRetries] BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_ChunkJobs_Revision", "[Revision] >= 1");
                    table.CheckConstraint("CK_ChunkJobs_Timeout", "[BatchTimeoutSeconds] BETWEEN 60 AND 600");
                    table.ForeignKey(
                        name: "FK_ChunkJobs_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChunkJobs_DocumentId_Status",
                table: "ChunkJobs",
                columns: new[] { "DocumentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ChunkJobs_Status_LeaseUntil",
                table: "ChunkJobs",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_ChunkJobs_WorkspaceId_CreatedAt",
                table: "ChunkJobs",
                columns: new[] { "WorkspaceId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChunkJobs");
        }
    }
}
