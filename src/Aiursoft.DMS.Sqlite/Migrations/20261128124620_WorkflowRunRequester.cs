using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.Sqlite.Migrations
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
                type: "TEXT",
                maxLength: 450,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE WorkflowRuns
                SET RequesterId = COALESCE(
                    (SELECT launch.RequesterId FROM WorkflowLaunches AS launch WHERE launch.RootId = WorkflowRuns.RootId),
                    (SELECT box.RequesterId FROM Lockboxes AS box WHERE box.Id = WorkflowRuns.LockboxId)
                )
                WHERE Origin = 0 AND RequesterId IS NULL
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
