using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionEventExtractionBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExtractedAt",
                table: "SessionEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExtractionBatchId",
                table: "SessionEvents",
                type: "TEXT",
                nullable: true);

            // Events recorded before batching existed were already covered by the old
            // summary-chunk extraction; without this the first new event of any old session would
            // claim its entire history as one batch.
            migrationBuilder.Sql(
                "UPDATE SessionEvents SET ExtractionBatchId = '00000000-0000-0000-0000-000000000000', " +
                "ExtractedAt = strftime('%Y-%m-%d %H:%M:%S+00:00', 'now');");

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvents_SessionId_ExtractionBatchId",
                table: "SessionEvents",
                columns: new[] { "SessionId", "ExtractionBatchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SessionEvents_SessionId_ExtractionBatchId",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "ExtractedAt",
                table: "SessionEvents");

            migrationBuilder.DropColumn(
                name: "ExtractionBatchId",
                table: "SessionEvents");
        }
    }
}
