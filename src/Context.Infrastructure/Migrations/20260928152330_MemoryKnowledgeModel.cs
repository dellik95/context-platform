using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MemoryKnowledgeModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Native DROP COLUMN: EF's DropColumn rebuilds Memories after the data SQL below and runs
            // PRAGMA foreign_keys outside the migration transaction.
            migrationBuilder.Sql("ALTER TABLE Memories DROP COLUMN IsCompacted;");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Memories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelKey",
                table: "MemoryEmbeddings",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceCount",
                table: "Memories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "Memories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TopicKey",
                table: "Memories",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemoryConsolidationReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryConsolidationReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryConsolidationReports_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryEntities",
                columns: table => new
                {
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Display = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEntities", x => new { x.MemoryId, x.Key });
                    table.ForeignKey(
                        name: "FK_MemoryEntities_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemoryEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Relation = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceMemoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryEvidence_Memories_MemoryId",
                        column: x => x.MemoryId,
                        principalTable: "Memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_ProjectId_Status",
                table: "Memories",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Memories_ProjectId_TopicKey",
                table: "Memories",
                columns: new[] { "ProjectId", "TopicKey" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryConsolidationReports_ProjectId_Status",
                table: "MemoryConsolidationReports",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEntities_ProjectId_Key",
                table: "MemoryEntities",
                columns: new[] { "ProjectId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEvidence_MemoryId",
                table: "MemoryEvidence",
                column: "MemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEvidence_ProjectId_SourceMemoryId",
                table: "MemoryEvidence",
                columns: new[] { "ProjectId", "SourceMemoryId" });

            // Old MemoryType ordinals: Decision 0, Discovery 1, BugFix 2, Feature 3, Refactor 4, Change 5,
            // Exploration 6, Architecture 7, Issue 8, Instruction 9, Implementation 10, Configuration 11,
            // Testing 12, Infrastructure 13. One CASE so no row is remapped twice.
            migrationBuilder.Sql("""
                UPDATE Memories SET Type = CASE Type
                    WHEN 0 THEN 1 WHEN 1 THEN 6 WHEN 2 THEN 6 WHEN 3 THEN 9 WHEN 4 THEN 9 WHEN 5 THEN 9
                    WHEN 6 THEN 9 WHEN 7 THEN 3 WHEN 8 THEN 8 WHEN 9 THEN 2 WHEN 10 THEN 4 WHEN 11 THEN 5
                    WHEN 12 THEN 7 WHEN 13 THEN 3 ELSE 0 END;
                """);

            // The supersede time was never recorded; the old row's last reinforcement is the closest bound.
            migrationBuilder.Sql("UPDATE Memories SET Status = 2, SupersededAt = LastReinforcedAt WHERE SupersededById IS NOT NULL;");

            migrationBuilder.Sql("""
                INSERT INTO MemoryEvidence (Id, ProjectId, MemoryId, Relation, Source, SessionId, SourceMemoryId, ObservedAt, Note)
                SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' ||
                             hex(randomblob(2)) || '-' || hex(randomblob(6))),
                       ProjectId, Id, 0, 5, CreatedFromSessionId, NULL, CreatedAt, 'migrated'
                FROM Memories;
                """);

            // CompactMemory (11) and its settings no longer exist.
            migrationBuilder.Sql("DELETE FROM Jobs WHERE Type = 11;");
            migrationBuilder.Sql("""
                UPDATE Projects SET ConfigJson = json_remove(ConfigJson,
                    '$.MemoryCompactionIntervalMinutes', '$.LastMemoryCompactionScheduledAt',
                    '$.MemoryCompactionSimilarityThreshold', '$.MemoryConsolidationSimilarityThreshold',
                    '$.MemoryConsolidationLowSimilarityThreshold', '$.MemoryConsolidationPromotionReinforcementThreshold',
                    '$.MemoryConsolidationTopKForDedup')
                WHERE json_valid(ConfigJson);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryConsolidationReports");

            migrationBuilder.DropTable(
                name: "MemoryEntities");

            migrationBuilder.DropTable(
                name: "MemoryEvidence");

            migrationBuilder.DropIndex(
                name: "IX_Memories_ProjectId_Status",
                table: "Memories");

            migrationBuilder.DropIndex(
                name: "IX_Memories_ProjectId_TopicKey",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "ModelKey",
                table: "MemoryEmbeddings");

            migrationBuilder.DropColumn(
                name: "SourceCount",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "TopicKey",
                table: "Memories");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Memories");

            migrationBuilder.AddColumn<bool>(
                name: "IsCompacted",
                table: "Memories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
