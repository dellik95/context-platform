using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryScopePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScopePath",
                table: "Memories",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memories_ProjectId_ScopePath",
                table: "Memories",
                columns: new[] { "ProjectId", "ScopePath" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Memories_ProjectId_ScopePath",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "ScopePath",
                table: "Memories");
        }
    }
}
