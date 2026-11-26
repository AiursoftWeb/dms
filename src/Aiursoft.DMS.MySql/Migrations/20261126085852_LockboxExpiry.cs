using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.MySql.Migrations
{
    /// <inheritdoc />
    public partial class LockboxExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Historical requests keep a uniform 4-hour lifetime from submission.
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Lockboxes",
                type: "datetime(6)",
                nullable: true);
            migrationBuilder.Sql("""
                UPDATE Lockboxes
                SET ExpiresAt = DATE_ADD(CreatedAt, INTERVAL 4 HOUR)
                """);
            migrationBuilder.AlterColumn<DateTime>(
                name: "ExpiresAt",
                table: "Lockboxes",
                type: "datetime(6)",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "datetime(6)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Lockboxes");
        }
    }
}
