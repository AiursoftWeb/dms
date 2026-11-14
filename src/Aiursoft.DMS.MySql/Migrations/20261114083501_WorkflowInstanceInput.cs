using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.MySql.Migrations
{
    public partial class WorkflowInstanceInput : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Input",
                table: "WorkflowRuns",
                type: "longtext",
                nullable: false,
                defaultValue: "{}")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Version",
                table: "WorkflowRuns",
                type: "varchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "2")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Input", table: "WorkflowRuns");
            migrationBuilder.DropColumn(name: "Version", table: "WorkflowRuns");
        }
    }
}
