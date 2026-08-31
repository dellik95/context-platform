using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryConceptsAndFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConceptsJson",
                table: "Memories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilesModifiedJson",
                table: "Memories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilesReadJson",
                table: "Memories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConceptsJson",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "FilesModifiedJson",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "FilesReadJson",
                table: "Memories");
        }
    }
}
