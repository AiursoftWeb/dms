using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.MySql.Migrations
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public partial class WorkflowRunRequester : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequesterId",
                table: "WorkflowRuns",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.Sql("""
                UPDATE WorkflowRuns AS run
                LEFT JOIN WorkflowLaunches AS launch ON launch.RootId = run.RootId
                LEFT JOIN Lockboxes AS box ON box.Id = run.LockboxId
                SET run.RequesterId = COALESCE(launch.RequesterId, box.RequesterId)
                WHERE run.Origin = 0 AND run.RequesterId IS NULL
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_RequesterId_CreatedAt",
                table: "WorkflowRuns",
                columns: new[] { "RequesterId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_RequesterId_CreatedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "RequesterId",
                table: "WorkflowRuns");
        }
    }
}
