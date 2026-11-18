using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicalRag.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrustedTestEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CaseRevision",
                table: "TestRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "TestRuns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "TestCases",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "TestCases",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Legacy rows cannot prove the tested case revision. Remove text-bearing legacy
            // output/diffs without deleting run history or authoring inputs/expectations.
            migrationBuilder.Sql("""
                EXEC(N'UPDATE [TestCases] SET [UpdatedAt] = SYSUTCDATETIME(), [LastRunJson] = NULL, [LastRunAt] = NULL;');
                EXEC(N'UPDATE [TestRuns] SET [Diff] = N''Legacy test evidence is untrusted. Rerun tests.'',
                    [FailureCode] = CASE WHEN [Status] = N''failed'' THEN N''invalid_result'' ELSE NULL END;');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TestCases_WorkspaceId",
                table: "TestCases",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TestCases_WorkspaceId",
                table: "TestCases");

            migrationBuilder.DropColumn(
                name: "CaseRevision",
                table: "TestRuns");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "TestRuns");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "TestCases");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "TestCases");
        }
    }
}
