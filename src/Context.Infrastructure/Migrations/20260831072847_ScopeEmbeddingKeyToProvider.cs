using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeEmbeddingKeyToProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Embeddings",
                table: "Embeddings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Embeddings",
                table: "Embeddings",
                columns: new[] { "ContentHash", "Provider" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Embeddings",
                table: "Embeddings");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Embeddings",
                table: "Embeddings",
                column: "ContentHash");
        }
    }
}
