using System.Text.Json;
using Context.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Context.Infrastructure.Tests;

/// <summary>Seeds Event (9) memories with their dependent rows at the previous migration, then migrates to
/// latest and checks that only they are gone.</summary>
public sealed class RemoveEventMemoriesMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260928152330_MemoryKnowledgeModel";

    private static readonly Guid ProjectId = Guid.NewGuid();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-migration-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ContextDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private static string Id(Guid id) => id.ToString().ToUpperInvariant();

    [Fact]
    public async Task Migration_deletes_event_memories_with_their_rows_and_renames_report_keys()
    {
        var at = "2026-09-01 10:00:00+00:00";
        var knowledge = Guid.NewGuid();
        var evt = Guid.NewGuid();
        var supersededByEvent = Guid.NewGuid();
        var pendingReport = Guid.NewGuid();
        var appliedReport = Guid.NewGuid();

        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var sql = $"INSERT INTO Projects (Id, Name, RootPath, CreatedAt) VALUES ('{Id(ProjectId)}', 'P', '/repo', '{at}');";
            foreach (var (id, type, status, supersededBy) in new[]
                     {
                         (knowledge, 1, 0, "NULL"),
                         (evt, 9, 0, "NULL"),
                         (supersededByEvent, 0, 2, $"'{Id(evt)}'"),
                     })
            {
                sql += "INSERT INTO Memories (Id, ProjectId, Type, Title, Body, Tier, Pinned, ReinforcementCount, SupersededById, SupersededAt, " +
                    "CreatedAt, LastReinforcedAt, Status, SourceCount) VALUES " +
                    $"('{Id(id)}', '{Id(ProjectId)}', {type}, 'T', 'B', 0, 0, 0, {supersededBy}, {(supersededBy == "NULL" ? "NULL" : $"'{at}'")}, " +
                    $"'{at}', '{at}', {status}, 1);" +
                    "INSERT INTO MemoryEvidence (Id, ProjectId, MemoryId, Relation, Source, ObservedAt) VALUES " +
                    $"('{Id(Guid.NewGuid())}', '{Id(ProjectId)}', '{Id(id)}', 0, 0, '{at}');" +
                    $"INSERT INTO MemoryEntities (MemoryId, Key, ProjectId, Display) VALUES ('{Id(id)}', 'symbol:foo', '{Id(ProjectId)}', 'Foo');" +
                    $"INSERT INTO MemoryEmbeddings (MemoryId, Vector, Dim, ModelKey) VALUES ('{Id(id)}', x'0000803f', 1, 'm');";
            }

            const string pendingJson =
                """{"groups":[],"reclassifiedEvents":[{"id":"x","title":"Added X","status":"Active","lastReinforcedAt":"2026-09-01T10:00:00+00:00","reason":"r"}],"untouched":0,"stats":{"groups":0,"reclassifiedEvents":1,"usedModel":false},"outcome":null}""";
            const string appliedJson =
                """{"groups":[],"reclassifiedEvents":[],"untouched":0,"stats":{"groups":0,"reclassifiedEvents":0,"usedModel":false},"outcome":{"appliedGroups":[],"reclassifiedEvents":2,"skippedEvents":["y"]}}""";
            sql += "INSERT INTO MemoryConsolidationReports (Id, ProjectId, CreatedAt, Status, ReportJson) VALUES " +
                $"('{Id(pendingReport)}', '{Id(ProjectId)}', '{at}', 0, '{pendingJson}'), " +
                $"('{Id(appliedReport)}', '{Id(ProjectId)}', '{at}', 2, '{appliedJson}');";
            await db.Database.ExecuteSqlRawAsync(sql.Replace("{", "{{").Replace("}", "}}"));

            await db.Database.MigrateAsync();
        }

        await using var readDb = CreateContext();
        var memories = await readDb.Memories.AsNoTracking().ToDictionaryAsync(m => m.Id);
        memories.Keys.Should().BeEquivalentTo([knowledge, supersededByEvent]);
        memories[supersededByEvent].Status.Should().Be(MemoryStatus.Obsolete);
        memories[supersededByEvent].SupersededById.Should().BeNull();
        memories[supersededByEvent].SupersededAt.Should().BeNull();
        memories[knowledge].Status.Should().Be(MemoryStatus.Active);

        (await readDb.MemoryEvidence.AsNoTracking().Select(e => e.MemoryId).ToListAsync()).Should().NotContain(evt).And.HaveCount(2);
        (await readDb.MemoryEntities.AsNoTracking().Select(e => e.MemoryId).ToListAsync()).Should().NotContain(evt).And.HaveCount(2);
        (await readDb.MemoryEmbeddings.AsNoTracking().Select(e => e.MemoryId).ToListAsync()).Should().NotContain(evt).And.HaveCount(2);

        var reports = await readDb.MemoryConsolidationReports.AsNoTracking().ToDictionaryAsync(r => r.Id, r => JsonDocument.Parse(r.ReportJson).RootElement);
        var pending = reports[pendingReport];
        pending.TryGetProperty("reclassifiedEvents", out _).Should().BeFalse();
        pending.GetProperty("retiredChangeLogs").EnumerateArray().Single().GetProperty("title").GetString().Should().Be("Added X");
        pending.GetProperty("stats").GetProperty("retiredChangeLogs").GetInt32().Should().Be(1);
        pending.GetProperty("outcome").ValueKind.Should().Be(JsonValueKind.Null);

        var outcome = reports[appliedReport].GetProperty("outcome");
        outcome.GetProperty("retiredChangeLogs").GetInt32().Should().Be(2);
        outcome.GetProperty("skippedChangeLogs").EnumerateArray().Single().GetString().Should().Be("y");
        outcome.TryGetProperty("reclassifiedEvents", out _).Should().BeFalse();
        outcome.TryGetProperty("skippedEvents", out _).Should().BeFalse();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
