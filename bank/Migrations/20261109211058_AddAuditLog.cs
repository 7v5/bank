using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleBankMvc.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    Details = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedUtc",
                table: "AuditLogs",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TargetId",
                table: "AuditLogs",
                column: "TargetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");
        }
    }
}
