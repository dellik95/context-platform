using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectBearerToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BearerTokenHash",
                table: "Projects",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BearerTokenIssuedAt",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_BearerTokenHash",
                table: "Projects",
                column: "BearerTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_BearerTokenHash",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BearerTokenHash",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BearerTokenIssuedAt",
                table: "Projects");
        }
    }
}
