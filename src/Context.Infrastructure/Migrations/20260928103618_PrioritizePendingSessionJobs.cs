using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PrioritizePendingSessionJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SummarizeSession (6) and ExtractMemory (7) now enqueue at JobPriority.Session; lift
            // the ones already waiting so they stop queuing behind the indexing backlog.
            migrationBuilder.Sql("UPDATE Jobs SET Priority = 10 WHERE Type IN (6, 7) AND Status IN (0, 3);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Jobs SET Priority = 0 WHERE Type IN (6, 7) AND Priority = 10;");
        }
    }
}
