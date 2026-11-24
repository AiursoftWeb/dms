using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.Sqlite.Migrations
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public partial class IndependentLockbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve the old approval wrapper and every historical callstack/trace ID.
            migrationBuilder.RenameColumn(name: "WorkflowId", table: "Lockboxes", newName: "LegacyApprovalWorkflowId");
            migrationBuilder.RenameColumn(name: "OperationId", table: "Lockboxes", newName: "WorkflowId");
            migrationBuilder.AlterColumn<Guid>(
                name: "LegacyApprovalWorkflowId", table: "Lockboxes", type: "TEXT", nullable: true,
                oldClrType: typeof(Guid), oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Independent Lockboxes cannot be downgraded without fabricating approval workflows. Restore a pre-upgrade database together with its matching Controller version.");
        }
    }
}
