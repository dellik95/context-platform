using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobStatusPriorityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_Status",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status_Priority",
                table: "Jobs",
                columns: new[] { "Status", "Priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_Status_Priority",
                table: "Jobs");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Status",
                table: "Jobs",
                column: "Status");
        }
    }
}
