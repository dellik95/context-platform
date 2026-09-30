using Context.Domain.Entities;
using Context.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Context.Infrastructure.Tests;

/// <summary>
/// Seeds rows in the pre-MemoryKnowledgeModel schema with raw SQL, then migrates through
/// MemoryKnowledgeModel and checks the data rewrite.
/// </summary>
public sealed class MemoryKnowledgeModelMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260928103618_PrioritizePendingSessionJobs";
    private const string TargetMigration = "20260928152330_MemoryKnowledgeModel";

    // The Event ordinal this migration mapped change-log types to; RemoveEventMemories deletes those rows.
    private const MemoryType Event = (MemoryType)9;

    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();

    private static readonly (int Old, MemoryType New)[] TypeRemap =
    [
        (0, MemoryType.Decision), (1, MemoryType.Discovery), (2, MemoryType.Discovery), (3, Event),
        (4, Event), (5, Event), (6, Event), (7, MemoryType.Architecture),
        (8, MemoryType.Issue), (9, MemoryType.Constraint), (10, MemoryType.Behavior), (11, MemoryType.Configuration),
        (12, MemoryType.Procedure), (13, MemoryType.Architecture),
    ];

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-migration-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    private static string Id(Guid id) => id.ToString().ToUpperInvariant();

    private static string At(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz");

    [Fact]
    public async Task Migration_remaps_types_records_status_and_seeds_legacy_evidence()
    {
        var created = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var reinforced = created.AddDays(3);
        var memoryIds = TypeRemap.ToDictionary(t => t.Old, _ => Guid.NewGuid());
        var supersededOld = memoryIds[1];
        var supersededNew = memoryIds[0];
        var compacted = memoryIds[2];

        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var sql = "INSERT INTO Projects (Id, Name, RootPath, CreatedAt, ConfigJson) VALUES " +
                $"('{Id(ProjectId)}', 'P', '/repo', '{At(created)}', " +
                "'{{\"ReindexEnabled\":true,\"MemoryCompactionIntervalMinutes\":5,\"MemoryConsolidationTopKForDedup\":3}}');" +
                $"INSERT INTO Sessions (Id, ProjectId, StartedAt, Status) VALUES ('{Id(SessionId)}', '{Id(ProjectId)}', '{At(created)}', 1);";

            foreach (var (old, _) in TypeRemap)
            {
                var id = memoryIds[old];
                var supersededBy = id == supersededOld ? $"'{Id(supersededNew)}'" : "NULL";
                var session = old % 2 == 0 ? $"'{Id(SessionId)}'" : "NULL";
                var isCompacted = id == compacted ? 1 : 0;
                sql += "INSERT INTO Memories (Id, ProjectId, Type, Title, Body, Tier, Pinned, ReinforcementCount, SupersededById, " +
                    "CreatedFromSessionId, CreatedAt, LastReinforcedAt, IsCompacted) VALUES " +
                    $"('{Id(id)}', '{Id(ProjectId)}', {old}, 'T{old}', 'B{old}', 0, 0, 2, {supersededBy}, {session}, " +
                    $"'{At(created)}', '{At(reinforced)}', {isCompacted});";
            }

            sql += "INSERT INTO Jobs (Id, Type, PayloadJson, Status, Priority, Attempts, CreatedAt) VALUES " +
                $"('{Id(Guid.NewGuid())}', 11, 'p', 0, 0, 0, '{At(created)}'), " +
                $"('{Id(Guid.NewGuid())}', 7, 'p', 0, 10, 0, '{At(created)}');";
            await db.Database.ExecuteSqlRawAsync(sql);

            await db.GetService<IMigrator>().MigrateAsync(TargetMigration);
        }

        await using var readDb = CreateContext();
        var memories = await readDb.Memories.AsNoTracking().ToDictionaryAsync(m => m.Id);

        memories.Should().HaveCount(TypeRemap.Length);
        foreach (var (old, expected) in TypeRemap)
        {
            memories[memoryIds[old]].Type.Should().Be(expected, "old ordinal {0} maps to {1}", old, expected);
        }

        memories[supersededOld].Status.Should().Be(MemoryStatus.Superseded);
        memories[supersededOld].SupersededById.Should().Be(supersededNew);
        memories[supersededOld].SupersededAt.Should().Be(reinforced);
        memories.Values.Where(m => m.Id != supersededOld).Should().OnlyContain(m => m.Status == MemoryStatus.Active && m.SupersededAt == null);
        memories[compacted].IsLive.Should().BeTrue();
        memories.Values.Should().OnlyContain(m => m.SourceCount == 1 && m.TopicKey == null);

        var evidence = await readDb.MemoryEvidence.AsNoTracking().ToListAsync();
        evidence.Should().HaveCount(TypeRemap.Length);
        evidence.Select(e => e.MemoryId).Should().BeEquivalentTo(memoryIds.Values);
        evidence.Should().OnlyContain(e =>
            e.ProjectId == ProjectId &&
            e.Relation == EvidenceRelation.Created &&
            e.Source == EvidenceSource.Legacy &&
            e.ObservedAt == created &&
            e.SourceMemoryId == null &&
            e.Note == "migrated" &&
            e.Id != Guid.Empty);
        evidence.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        evidence.Single(e => e.MemoryId == memoryIds[0]).SessionId.Should().Be(SessionId);
        evidence.Single(e => e.MemoryId == memoryIds[1]).SessionId.Should().BeNull();

        (await readDb.Jobs.AsNoTracking().Select(j => j.Type).ToListAsync()).Should().Equal(JobType.ExtractMemory);
        (await readDb.Projects.AsNoTracking().SingleAsync()).ConfigJson.Should().Be("""{"ReindexEnabled":true}""");
    }

    [Fact]
    public async Task Migration_leaves_ModelKey_null_so_legacy_embeddings_count_as_stale()
    {
        var created = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var memoryId = Guid.NewGuid();

        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            var sql =
                $"INSERT INTO Projects (Id, Name, RootPath, CreatedAt) VALUES ('{Id(ProjectId)}', 'P', '/repo', '{At(created)}');" +
                $"INSERT INTO Embeddings (ContentHash, Provider, CreatedAt, Dim, Model, Vector) VALUES ('h1', 'ollama:a', '{At(created)}', 4, 'a', x'00');" +
                "INSERT INTO Memories (Id, ProjectId, Type, Title, Body, Tier, Pinned, ReinforcementCount, CreatedAt, LastReinforcedAt) " +
                $"VALUES ('{Id(memoryId)}', '{Id(ProjectId)}', 0, 'T', 'B', 0, 0, 0, '{At(created)}', '{At(created)}');" +
                $"INSERT INTO MemoryEmbeddings (MemoryId, Vector, Dim) VALUES ('{Id(memoryId)}', x'00', 4);";
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.MigrateAsync();
        }

        await using var readDb = CreateContext();
        (await readDb.MemoryEmbeddings.AsNoTracking().SingleAsync(e => e.MemoryId == memoryId)).ModelKey.Should().BeNull(
            "a dimension shared with a cached chunk provider does not prove which model embedded the memory");
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
