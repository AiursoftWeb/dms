using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.DMS.Sqlite.Migrations
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public partial class AgentEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JoinKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClusterId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssuerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JoinKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JoinKeys_AspNetUsers_IssuerId",
                        column: x => x.IssuerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JoinKeys_Clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "Clusters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentEnrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JoinKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LockboxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReceiptHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AgentKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MachineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEnrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentEnrollments_JoinKeys_JoinKeyId",
                        column: x => x.JoinKeyId,
                        principalTable: "JoinKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEnrollments_Lockboxes_LockboxId",
                        column: x => x.LockboxId,
                        principalTable: "Lockboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEnrollments_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Machines_InstallationId",
                table: "Machines",
                column: "InstallationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_AgentKeyHash",
                table: "AgentEnrollments",
                column: "AgentKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_InstallationId",
                table: "AgentEnrollments",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_JoinKeyId",
                table: "AgentEnrollments",
                column: "JoinKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_LockboxId",
                table: "AgentEnrollments",
                column: "LockboxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_MachineId",
                table: "AgentEnrollments",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEnrollments_ReceiptHash",
                table: "AgentEnrollments",
                column: "ReceiptHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JoinKeys_ClusterId",
                table: "JoinKeys",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_JoinKeys_IssuerId",
                table: "JoinKeys",
                column: "IssuerId");

            migrationBuilder.CreateIndex(
                name: "IX_JoinKeys_KeyHash",
                table: "JoinKeys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEnrollments");

            migrationBuilder.DropTable(
                name: "JoinKeys");

            migrationBuilder.DropIndex(
                name: "IX_Machines_InstallationId",
                table: "Machines");
        }
    }
}
