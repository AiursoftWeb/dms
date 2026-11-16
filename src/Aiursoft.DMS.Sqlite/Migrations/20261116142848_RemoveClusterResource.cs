using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RemoveClusterResource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF rebuilds JoinKeys after processing the table drops. Disable FK enforcement before that rebuild.
            migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
            migrationBuilder.Sql("UPDATE Lockboxes SET Parameters = json_remove(Parameters, '$.ClusterId')");
            migrationBuilder.Sql("UPDATE WorkflowAuditOutbox SET Metadata = json_remove(Metadata, '$.ClusterId')");

            migrationBuilder.DropForeignKey(
                name: "FK_JoinKeys_Clusters_ClusterId",
                table: "JoinKeys");

            migrationBuilder.DropTable(
                name: "ClusterGrants");

            migrationBuilder.DropTable(
                name: "Clusters");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_ClusterId_CreatedAt",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_JoinKeys_ClusterId",
                table: "JoinKeys");

            migrationBuilder.DropColumn(
                name: "ClusterId",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "ClusterId",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "ClusterId",
                table: "Lockboxes");

            migrationBuilder.DropColumn(
                name: "ClusterId",
                table: "JoinKeys");

            migrationBuilder.DropColumn(
                name: "CanApproveOwnRequests",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_CreatedAt",
                table: "WorkflowRuns",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Cluster removal cannot reconstruct former memberships. Restore the pre-upgrade database to roll back.");
    }
}
