using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropDormantMemoryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryFiles");

            migrationBuilder.DropTable(
                name: "MemoryRelations");

            migrationBuilder.DropTable(
                name: "MemorySymbols");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryFiles",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryFiles", x => new { x.MemoryId, x.FileId });
                    table.ForeignKey(
                        name: "FK_MemoryFiles_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemoryFiles_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelatedMemoryId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryRelations_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MemoryRelations_Memories_RelatedMemoryId",
                        column: x => x.RelatedMemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MemorySymbols",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SymbolId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemorySymbols", x => new { x.MemoryId, x.SymbolId });
                    table.ForeignKey(
                        name: "FK_MemorySymbols_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemorySymbols_Symbols_SymbolId",
                        column: x => x.SymbolId,
                        principalTable: "Symbols",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryFiles_FileId",
                table: "MemoryFiles",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryRelations_MemoryId",
                table: "MemoryRelations",
                column: "MemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryRelations_RelatedMemoryId",
                table: "MemoryRelations",
                column: "RelatedMemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MemorySymbols_SymbolId",
                table: "MemorySymbols",
                column: "SymbolId");
        }
    }
}
