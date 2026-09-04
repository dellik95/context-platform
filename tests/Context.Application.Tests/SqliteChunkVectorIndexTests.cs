using Context.Application.Adapters;
using Context.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Context.Application.Tests;

/// <summary>
/// Real, on-disk-SQLite coverage for <see cref="SqliteChunkVectorIndex.DeleteByProjectAsync"/> --
/// the cleanup step "delete a project" needs because this sqlite-vec-backed table is a separate
/// custom store, not an EF-mapped entity Project's cascade-delete FK configuration ever reaches
/// (see <c>IProjectRepository.DeleteAsync</c>'s remarks). Exercises the real
/// <c>Microsoft.SemanticKernel.Connectors.SqliteVec</c> collection end to end, not a fake.
/// </summary>
public sealed class SqliteChunkVectorIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-vectorindex-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task DeleteByProjectAsync_removes_only_the_given_projects_records_from_every_provider_collection()
    {
        // A real (migrated, PRAGMA foreign_keys-enabled) ContextDbContext isn't strictly required
        // for this adapter -- it opens its own connection to the same file -- but creating the
        // database file up front through the real DbContext keeps this test's setup identical to
        // how production reaches this file (via AddContextApplication's shared dbPath).
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        await using (var db = new ContextDbContext(optionsBuilder.Options))
        {
            await db.Database.MigrateAsync();
        }

        var dbContextFactory = new PooledDbContextFactoryStub(_dbPath);
        using var index = new SqliteChunkVectorIndex(_dbPath, dbContextFactory);

        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var chunkA1 = Guid.NewGuid();
        var chunkA2 = Guid.NewGuid();
        var chunkB1 = Guid.NewGuid();

        // Two providers, to prove deletion sweeps every chunk_vectors_* table, not just the first.
        await index.UpsertAsync(chunkA1, projectA, "ollama", [1f, 0f, 0f], CancellationToken.None);
        await index.UpsertAsync(chunkA2, projectA, "ollama", [0f, 1f, 0f], CancellationToken.None);
        await index.UpsertAsync(chunkB1, projectB, "ollama", [0f, 0f, 1f], CancellationToken.None);
        await index.UpsertAsync(chunkA1, projectA, "openai", [1f, 0f, 0f, 0f], CancellationToken.None);

        await index.DeleteByProjectAsync(projectA, CancellationToken.None);

        // projectA's records are gone from both providers it had records in...
        (await index.SearchAsync(projectA, "ollama", [1f, 0f, 0f], limit: 10, CancellationToken.None)).Should().BeEmpty();
        (await index.SearchAsync(projectA, "openai", [1f, 0f, 0f, 0f], limit: 10, CancellationToken.None)).Should().BeEmpty();

        // ...but projectB's own record in the same "ollama" collection is untouched.
        var remaining = await index.SearchAsync(projectB, "ollama", [0f, 0f, 1f], limit: 10, CancellationToken.None);
        remaining.Should().ContainSingle(r => r.ChunkId == chunkB1);
    }

    [Fact]
    public async Task DeleteByProjectAsync_is_a_no_op_when_no_chunk_vector_tables_exist_yet()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        await using (var db = new ContextDbContext(optionsBuilder.Options))
        {
            await db.Database.MigrateAsync();
        }

        using var index = new SqliteChunkVectorIndex(_dbPath, new PooledDbContextFactoryStub(_dbPath));

        // Nothing was ever upserted -- no chunk_vectors_* table exists in this database file yet.
        // Must not throw.
        await index.DeleteByProjectAsync(Guid.NewGuid(), CancellationToken.None);
    }

    /// <summary>
    /// Minimal <see cref="IDbContextFactory{TContext}"/> so this test doesn't need the full
    /// pooled-factory DI registration <c>AddContextApplication</c> normally provides --
    /// <see cref="SqliteChunkVectorIndex"/>'s backfill path is the only thing that uses it, and
    /// this test never exercises a fresh-collection backfill.
    /// </summary>
    private sealed class PooledDbContextFactoryStub(string dbPath) : IDbContextFactory<ContextDbContext>
    {
        public ContextDbContext CreateDbContext()
        {
            var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
            return new ContextDbContext(optionsBuilder.Options);
        }

        public Task<ContextDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by connection string, so the underlying OS file
        // handle can outlive the DbContext's/collection's own Dispose() -- clear the pool first or
        // the file deletes below intermittently throw "process cannot access the file".
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
