using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHookInvocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HookInvocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HookInvocations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HookInvocations_ProjectId_ReceivedAt",
                table: "HookInvocations",
                columns: new[] { "ProjectId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HookInvocations");
        }
    }
}
