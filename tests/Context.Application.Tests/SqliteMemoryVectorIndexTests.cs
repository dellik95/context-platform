using Context.Application.Adapters;
using Context.Application.Support;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

public sealed class SqliteMemoryVectorIndexTests : IDisposable
{
    private const string ModelKey = "ollama:m";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-memoryvec-tests-{Guid.NewGuid():N}.db");

    private DbContextOptions<ContextDbContext> Options =>
        new DbContextOptionsBuilder<ContextDbContext>().UseSqlite($"Data Source={_dbPath}").Options;

    [Fact]
    public async Task First_use_drops_legacy_collections_and_backfills_only_live_memories_of_the_same_model()
    {
        var projectId = Guid.NewGuid();
        var live = Guid.NewGuid();
        var otherModel = Guid.NewGuid();
        var unknownModel = Guid.NewGuid();
        var superseded = Guid.NewGuid();
        float[] vector = [1f, 0f, 0f];

        await using (var db = new ContextDbContext(Options))
        {
            await db.Database.MigrateAsync();
            db.Projects.Add(new Project { Id = projectId, Name = "P", RootPath = "/repo", CreatedAt = DateTimeOffset.UtcNow });
            db.Memories.AddRange(
                NewMemory(live, projectId, MemoryStatus.Active),
                NewMemory(otherModel, projectId, MemoryStatus.Active),
                NewMemory(unknownModel, projectId, MemoryStatus.Contested),
                NewMemory(superseded, projectId, MemoryStatus.Obsolete));
            db.MemoryEmbeddings.AddRange(
                NewEmbedding(live, vector, ModelKey),
                NewEmbedding(otherModel, vector, "noop:noop"),
                NewEmbedding(unknownModel, vector, null),
                NewEmbedding(superseded, vector, ModelKey));
            await db.SaveChangesAsync();
        }

        await CreateLegacyCollectionAsync("memory_vectors_ollamam", projectId, superseded, vector);

        using var index = new SqliteMemoryVectorIndex(_dbPath, new FakeDbContextFactory(Options));
        var hits = await index.SearchAsync(projectId, ModelKey, vector, 10, CancellationToken.None);

        hits.Select(h => h.MemoryId).Should().Equal(live);
        (await SqliteVectorTables.ListAsync($"Data Source={_dbPath}", "memory_vectors_", CancellationToken.None)).Should().BeEmpty();
        (await SqliteVectorTables.ListAsync($"Data Source={_dbPath}", "memory_vec_", CancellationToken.None)).Should().Equal("memory_vec_ollamam");
    }

    [Fact]
    public async Task First_use_in_a_new_process_fills_only_the_vectors_that_are_missing()
    {
        var projectId = Guid.NewGuid();
        var indexed = Guid.NewGuid();
        var lost = Guid.NewGuid();
        float[] vector = [1f, 0f, 0f];

        await using (var db = new ContextDbContext(Options))
        {
            await db.Database.MigrateAsync();
            db.Projects.Add(new Project { Id = projectId, Name = "P", RootPath = "/repo", CreatedAt = DateTimeOffset.UtcNow });
            db.Memories.Add(NewMemory(indexed, projectId, MemoryStatus.Active));
            db.MemoryEmbeddings.Add(NewEmbedding(indexed, vector, ModelKey));
            await db.SaveChangesAsync();
        }

        using (var first = new SqliteMemoryVectorIndex(_dbPath, new FakeDbContextFactory(Options)))
        {
            await first.UpsertAsync(indexed, projectId, "Payments", ModelKey, vector, CancellationToken.None);
        }

        // Committed to the database, but the process died before the vector write.
        await using (var db = new ContextDbContext(Options))
        {
            db.Memories.Add(NewMemory(lost, projectId, MemoryStatus.Contested));
            db.MemoryEmbeddings.Add(NewEmbedding(lost, [0.9f, 0.1f, 0f], ModelKey));
            await db.SaveChangesAsync();
        }

        using var second = new SqliteMemoryVectorIndex(_dbPath, new FakeDbContextFactory(Options));
        var hits = await second.SearchAsync(projectId, ModelKey, vector, 10, CancellationToken.None);
        var byTopic = await second.SearchAsync(projectId, ModelKey, vector, 10, CancellationToken.None, topic: "Payments");

        hits.Select(h => h.MemoryId).Should().Equal(indexed, lost);
        byTopic.Select(h => h.MemoryId).Should().Equal([indexed], "a record already present is not overwritten from the embedding row");
    }

    private static DomainMemory NewMemory(Guid id, Guid projectId, MemoryStatus status) => new()
    {
        Id = id,
        ProjectId = projectId,
        Type = MemoryType.Fact,
        Title = "T",
        Body = "B",
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        LastReinforcedAt = DateTimeOffset.UtcNow,
    };

    private static MemoryEmbedding NewEmbedding(Guid memoryId, float[] vector, string? modelKey) => new()
    {
        MemoryId = memoryId,
        Vector = EmbeddingVectorCodec.Encode(vector),
        Dim = vector.Length,
        ModelKey = modelKey,
    };

    private async Task CreateLegacyCollectionAsync(string name, Guid projectId, Guid memoryId, float[] vector)
    {
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            {
                new VectorStoreKeyProperty(nameof(LegacyRecord.MemoryId), typeof(Guid)),
                new VectorStoreDataProperty(nameof(LegacyRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
                new VectorStoreVectorProperty(nameof(LegacyRecord.Vector), typeof(ReadOnlyMemory<float>), vector.Length)
                {
                    DistanceFunction = DistanceFunction.CosineDistance,
                },
            },
        };
        using var collection = new SqliteCollection<Guid, LegacyRecord>(
            $"Data Source={_dbPath}", name, new SqliteCollectionOptions { Definition = definition });
        await collection.EnsureCollectionExistsAsync();
        await collection.UpsertAsync(new LegacyRecord { MemoryId = memoryId, ProjectId = projectId, Vector = vector });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    private sealed class LegacyRecord
    {
        public required Guid MemoryId { get; set; }
        public required Guid ProjectId { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }

    private sealed class FakeDbContextFactory(DbContextOptions<ContextDbContext> options) : IDbContextFactory<ContextDbContext>
    {
        public ContextDbContext CreateDbContext() => new(options);
    }
}
