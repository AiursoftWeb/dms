using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.MySql.Migrations
{
    /// <inheritdoc />
    public partial class IndependentLockbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve the old approval wrapper and every historical callstack/trace ID.
            migrationBuilder.RenameColumn(name: "WorkflowId", table: "Lockboxes", newName: "LegacyApprovalWorkflowId");
            migrationBuilder.RenameColumn(name: "OperationId", table: "Lockboxes", newName: "WorkflowId");
            migrationBuilder.AlterColumn<Guid>(
                name: "LegacyApprovalWorkflowId", table: "Lockboxes", type: "char(36)", nullable: true,
                oldClrType: typeof(Guid), oldType: "char(36)")
                .Annotation("Relational:Collation", "ascii_general_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Independent Lockboxes cannot be downgraded without fabricating approval workflows. Restore a pre-upgrade database together with its matching Controller version.");
        }
    }
}
