using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSummaryChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SessionSummaryChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SequenceNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    FromEventAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ToEventAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Completed = table.Column<string>(type: "TEXT", nullable: true),
                    Discovered = table.Column<string>(type: "TEXT", nullable: true),
                    Decisions = table.Column<string>(type: "TEXT", nullable: true),
                    FilesReadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedFilesJson = table.Column<string>(type: "TEXT", nullable: true),
                    OpenIssues = table.Column<string>(type: "TEXT", nullable: true),
                    NextSteps = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSummaryChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSummaryChunks_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionSummaryChunks_SessionId_SequenceNumber",
                table: "SessionSummaryChunks",
                columns: new[] { "SessionId", "SequenceNumber" },
                unique: true);

            // Backfill: give every session that already has a current-picture SessionSummary a
            // single chunk #1 covering its whole history, so pre-existing sessions don't show a
            // gap once chunk-based history is the primary read path (see SessionSummaryChunk's
            // remarks). FromEventAt falls back to Sessions.StartedAt since SessionSummary itself
            // never recorded a range start.
            migrationBuilder.Sql(@"
                INSERT INTO SessionSummaryChunks (Id, SessionId, SequenceNumber, FromEventAt, ToEventAt, EventCount, Title, Completed, Discovered, Decisions, FilesReadJson, ChangedFilesJson, OpenIssues, NextSteps, GeneratedAt)
                SELECT lower(hex(randomblob(16))), SessionId, 1, (SELECT StartedAt FROM Sessions WHERE Sessions.Id = SessionSummaries.SessionId), GeneratedAt, 0, Title, Completed, Discovered, Decisions, FilesReadJson, ChangedFilesJson, OpenIssues, NextSteps, GeneratedAt
                FROM SessionSummaries;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionSummaryChunks");
        }
    }
}
