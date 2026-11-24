using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.Sqlite.Migrations
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public partial class WorkflowLaunchSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowLaunches",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Definition = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Parameters = table.Column<string>(type: "TEXT", nullable: false),
                    TargetMachines = table.Column<string>(type: "TEXT", nullable: false),
                    EnrollmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequesterId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ApproverId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Automatic = table.Column<bool>(type: "INTEGER", nullable: false),
                    Risk = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowLaunches", x => x.RootId);
                });

            // Use the execution's actual root so historical approval-wrapper trees keep their IDs.
            migrationBuilder.Sql("""
                INSERT INTO WorkflowLaunches
                    (RootId, Definition, Version, Parameters, TargetMachines, EnrollmentId,
                     RequesterId, ApproverId, Automatic, Risk, DecidedAt)
                SELECT r.RootId, b.Definition, b.Version, b.Parameters, b.TargetMachines, e.Id,
                       b.RequesterId, b.ApproverId, b.Automatic, b.Risk, b.DecidedAt
                FROM Lockboxes b
                JOIN WorkflowRuns r ON r.Id = b.WorkflowId AND r.LockboxId = b.Id
                LEFT JOIN AgentEnrollments e ON e.LockboxId = b.Id
                WHERE b.Decision = 1
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowLaunches");
        }
    }
}
