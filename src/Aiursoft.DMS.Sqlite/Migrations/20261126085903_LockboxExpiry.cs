using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class LockboxExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Historical requests keep a uniform 4-hour lifetime from submission.
            // EF stores DateTime as TEXT with full fractional precision; strftime only emits milliseconds.
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Lockboxes",
                type: "TEXT",
                nullable: true);
            migrationBuilder.Sql("""
                UPDATE Lockboxes
                SET ExpiresAt = strftime('%Y-%m-%d %H:%M:%f', CreatedAt, '+4 hours') || '0000'
                """);
            migrationBuilder.AlterColumn<DateTime>(
                name: "ExpiresAt",
                table: "Lockboxes",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "TEXT",
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
