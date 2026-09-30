using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Context.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BatchPendingEmbedChunkJobs : Migration
    {
        private const string NewGuid = """
            upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2)) || '-' ||
                  hex(randomblob(2)) || '-' || hex(randomblob(6)))
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EmbedChunk (5) payloads went from one content hash {ProjectId, ChunkIds, ContentHash, Text}
            // to a batch {ProjectId, Items: [{ContentHash, Text, ChunkIds}]}. Waiting jobs are merged per
            // project into batches of 16 items, one item per content hash (duplicate jobs for the same
            // hash pool their chunk ids). Running ones join them: a Running row at startup is orphaned.
            migrationBuilder.Sql($"""
                WITH Items AS (
                    SELECT j.ProjectId,
                           json_extract(j.PayloadJson, '$.ProjectId') AS PayloadProjectId,
                           json_extract(j.PayloadJson, '$.ContentHash') AS ContentHash,
                           min(json_extract(j.PayloadJson, '$.Text')) AS Text,
                           json_group_array(DISTINCT c.value) AS ChunkIds,
                           min(j.CreatedAt) AS CreatedAt,
                           max(j.Priority) AS Priority
                    FROM Jobs j, json_each(j.PayloadJson, '$.ChunkIds') c
                    WHERE j.Type = 5 AND j.Status IN (0, 1) AND json_type(j.PayloadJson, '$.ContentHash') IS NOT NULL
                    GROUP BY j.ProjectId, PayloadProjectId, ContentHash
                ),
                Batched AS (
                    SELECT *, (row_number() OVER (PARTITION BY ProjectId, PayloadProjectId ORDER BY CreatedAt, ContentHash) - 1) / 16 AS Batch
                    FROM Items
                )
                INSERT INTO Jobs (Id, Type, PayloadJson, Status, Priority, Attempts, CreatedAt, ProjectId)
                SELECT {NewGuid},
                       5,
                       json_object('ProjectId', PayloadProjectId, 'Items',
                           json_group_array(json_object('ContentHash', ContentHash, 'Text', Text, 'ChunkIds', json(ChunkIds)))),
                       0, max(Priority), 0, min(CreatedAt), ProjectId
                FROM Batched
                GROUP BY ProjectId, PayloadProjectId, Batch;
                """);
            migrationBuilder.Sql("DELETE FROM Jobs WHERE Type = 5 AND Status IN (0, 1) AND json_type(PayloadJson, '$.ContentHash') IS NOT NULL;");

            // Done and Failed rows keep their status as single-item batches, so a manual retry still runs.
            migrationBuilder.Sql("""
                UPDATE Jobs SET PayloadJson = json_object(
                        'ProjectId', json_extract(PayloadJson, '$.ProjectId'),
                        'Items', json_array(json_object(
                            'ContentHash', json_extract(PayloadJson, '$.ContentHash'),
                            'Text', json_extract(PayloadJson, '$.Text'),
                            'ChunkIds', json(coalesce(json_extract(PayloadJson, '$.ChunkIds'), '[]')))))
                WHERE Type = 5 AND json_type(PayloadJson, '$.ContentHash') IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                INSERT INTO Jobs (Id, Type, PayloadJson, Status, Priority, Attempts, LastError, CreatedAt, StartedAt, FinishedAt, NextRetryAt, ProjectId)
                SELECT {NewGuid},
                       5,
                       json_object('ProjectId', json_extract(j.PayloadJson, '$.ProjectId'),
                                   'ChunkIds', json(json_extract(i.value, '$.ChunkIds')),
                                   'ContentHash', json_extract(i.value, '$.ContentHash'),
                                   'Text', json_extract(i.value, '$.Text')),
                       j.Status, j.Priority, j.Attempts, j.LastError, j.CreatedAt, j.StartedAt, j.FinishedAt, j.NextRetryAt, j.ProjectId
                FROM Jobs j, json_each(j.PayloadJson, '$.Items') i
                WHERE j.Type = 5 AND json_type(j.PayloadJson, '$.Items') = 'array';
                """);
            migrationBuilder.Sql("DELETE FROM Jobs WHERE Type = 5 AND json_type(PayloadJson, '$.Items') = 'array';");
        }
    }
}
