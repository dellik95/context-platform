using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEventMemories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MemoryType.Event (9) no longer exists: memory is a knowledge base, not a change log. Mirrors
            // MemoryRepository.DeleteAsync. Events were never embedded into the memory_vec_* collections.
            migrationBuilder.Sql("""
                UPDATE Memories SET SupersededById = NULL, SupersededAt = NULL, Status = 3
                WHERE SupersededById IN (SELECT Id FROM Memories WHERE Type = 9);
                """);
            migrationBuilder.Sql("DELETE FROM MemoryEvidence WHERE MemoryId IN (SELECT Id FROM Memories WHERE Type = 9);");
            migrationBuilder.Sql("DELETE FROM MemoryEntities WHERE MemoryId IN (SELECT Id FROM Memories WHERE Type = 9);");
            migrationBuilder.Sql("DELETE FROM MemoryEmbeddings WHERE MemoryId IN (SELECT Id FROM Memories WHERE Type = 9);");
            migrationBuilder.Sql("DELETE FROM Memories WHERE Type = 9;");

            // Reconsolidation reports: reclassifying change logs as events became retiring them.
            RenameReportKeys(migrationBuilder, "reclassifiedEvents", "retiredChangeLogs", "skippedEvents", "skippedChangeLogs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RenameReportKeys(migrationBuilder, "retiredChangeLogs", "reclassifiedEvents", "skippedChangeLogs", "skippedEvents");
        }

        private static void RenameReportKeys(MigrationBuilder migrationBuilder, string list, string newList, string skipped, string newSkipped)
        {
            migrationBuilder.Sql($"""
                UPDATE MemoryConsolidationReports SET ReportJson = json_set(
                    json_remove(ReportJson, '$.{list}', '$.stats.{list}'),
                    '$.{newList}', json(COALESCE(json_extract(ReportJson, '$.{list}'), '[]')),
                    '$.stats.{newList}', COALESCE(json_extract(ReportJson, '$.stats.{list}'), 0))
                WHERE json_valid(ReportJson);
                """);
            migrationBuilder.Sql($"""
                UPDATE MemoryConsolidationReports SET ReportJson = json_set(
                    json_remove(ReportJson, '$.outcome.{list}', '$.outcome.{skipped}'),
                    '$.outcome.{newList}', COALESCE(json_extract(ReportJson, '$.outcome.{list}'), 0),
                    '$.outcome.{newSkipped}', json(COALESCE(json_extract(ReportJson, '$.outcome.{skipped}'), '[]')))
                WHERE json_valid(ReportJson) AND json_type(ReportJson, '$.outcome') = 'object';
                """);
        }
    }
}
