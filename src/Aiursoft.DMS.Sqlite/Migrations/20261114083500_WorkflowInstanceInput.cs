using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.Sqlite.Migrations
{
    [ExcludeFromCodeCoverage]
    public partial class WorkflowInstanceInput : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Input",
                table: "WorkflowRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "Version",
                table: "WorkflowRuns",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "2");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Input", table: "WorkflowRuns");
            migrationBuilder.DropColumn(name: "Version", table: "WorkflowRuns");
        }
    }
}
