using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.MySql.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowLaunchSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowLaunches",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Definition = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Version = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Parameters = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TargetMachines = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EnrollmentId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    RequesterId = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ApproverId = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Automatic = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Risk = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowLaunches", x => x.RootId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

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
