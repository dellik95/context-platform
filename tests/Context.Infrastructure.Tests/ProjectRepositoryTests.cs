using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Tests;

/// <summary>
/// Real, on-disk-SQLite coverage for <see cref="ProjectRepository.DeleteAsync"/> -- confirms the
/// FK cascade configuration (<c>FileConfiguration</c>, <c>SessionConfiguration</c>,
/// <c>MemoryConfiguration</c>, <c>GraphNodeConfiguration</c>, <c>GraphEdgeConfiguration</c>,
/// <c>ClaudeIntegrationConfiguration</c>, and transitively <c>ChunkConfiguration</c>/
/// <c>SymbolConfiguration</c> via File) actually removes every row a project transitively owns
/// when SQLite's own <c>ON DELETE CASCADE</c> fires (enabled via <c>PRAGMA foreign_keys=ON</c>,
/// see <c>ContextDbContext.SqlitePragmaConnectionInterceptor</c>) -- and that the one
/// project-scoped table cascade deliberately does NOT reach (<c>Embeddings</c>, a shared
/// content-hash-keyed cache) survives.
/// </summary>
public sealed class ProjectRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-projectrepo-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_a_project_that_does_not_exist()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var repository = new ProjectRepository(db);

        var result = await repository.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_cascades_every_row_the_project_owns_but_leaves_other_projects_and_the_shared_Embeddings_cache_alone()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "Doomed", RootPath = "/doomed", CreatedAt = DateTimeOffset.UtcNow };
        var otherProject = new Project { Id = Guid.NewGuid(), Name = "Survivor", RootPath = "/survivor", CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.AddRange(project, otherProject);

        var file = new DomainFile
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, RelativePath = "a.cs",
            ContentHash = "shared-hash", IsDeleted = false, UpdatedAt = DateTimeOffset.UtcNow
        };
        var otherFile = new DomainFile
        {
            Id = Guid.NewGuid(), ProjectId = otherProject.Id, RelativePath = "b.cs",
            ContentHash = "other-hash", IsDeleted = false, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Files.AddRange(file, otherFile);

        var symbol = new Symbol
        {
            Id = Guid.NewGuid(), FileId = file.Id, Kind = SymbolKind.Class, Name = "Foo", QualifiedName = "Ns.Foo",
            StartLine = 1, EndLine = 10, SignatureHash = "sig", BodyHash = "body"
        };
        // A child symbol (e.g. a method) whose ParentSymbolId points at another symbol in the SAME
        // file -- Symbol.ParentSymbolId is a self-referencing Restrict FK, and both symbols cascade
        // from the same File row simultaneously, so this is exactly the shape that broke a naive
        // Remove(project)+SaveChanges with "FOREIGN KEY constraint failed" (confirmed live against
        // a real reindexed project before ProjectRepository.DeleteAsync nulled ParentSymbolId first).
        var childSymbol = new Symbol
        {
            Id = Guid.NewGuid(), FileId = file.Id, ParentSymbolId = symbol.Id, Kind = SymbolKind.Method, Name = "Bar",
            QualifiedName = "Ns.Foo.Bar", StartLine = 2, EndLine = 3, SignatureHash = "sig2", BodyHash = "body2"
        };
        db.Symbols.AddRange(symbol, childSymbol);

        var chunk = new Chunk
        {
            Id = Guid.NewGuid(), FileId = file.Id, SymbolId = symbol.Id, Text = "content",
            TokenCount = 1, ContentHash = "shared-hash"
        };
        db.Chunks.Add(chunk);

        // A content-hash-keyed embedding, deliberately shared/deduped across projects (see
        // EmbedChunkJobHandler's remarks) -- must survive the deleted project entirely.
        db.Embeddings.Add(new Embedding
        {
            ContentHash = "shared-hash", Provider = "noop", Model = "noop", Dim = 1,
            Vector = [0], CreatedAt = DateTimeOffset.UtcNow
        });

        var session = new Session { Id = Guid.NewGuid(), ProjectId = project.Id, StartedAt = DateTimeOffset.UtcNow };
        db.Sessions.Add(session);
        db.SessionEvents.Add(new SessionEvent
        {
            Id = Guid.NewGuid(), SessionId = session.Id, Type = SessionEventType.Prompt,
            PayloadJson = "{}", Timestamp = DateTimeOffset.UtcNow
        });

        var memory = new Memory
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Type = MemoryType.Discovery, Title = "T", Body = "B",
            Tier = MemoryTier.Working, CreatedAt = DateTimeOffset.UtcNow, LastReinforcedAt = DateTimeOffset.UtcNow
        };
        db.Memories.Add(memory);

        // A second memory this one supersedes -- Memory.SupersededById is a self-referencing
        // Restrict FK, both cascade from the same Project row, same hazard class as
        // Symbol.ParentSymbolId above.
        var supersededMemory = new Memory
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Type = MemoryType.Discovery, Title = "Old", Body = "B",
            Tier = MemoryTier.Working, SupersededById = memory.Id,
            CreatedAt = DateTimeOffset.UtcNow, LastReinforcedAt = DateTimeOffset.UtcNow
        };
        db.Memories.Add(supersededMemory);

        var graphNodeA = new GraphNode { Id = Guid.NewGuid(), ProjectId = project.Id, Kind = GraphNodeKind.File, Label = "a.cs" };
        var graphNodeB = new GraphNode { Id = Guid.NewGuid(), ProjectId = project.Id, Kind = GraphNodeKind.Class, Label = "Foo" };
        db.GraphNodes.AddRange(graphNodeA, graphNodeB);

        // GraphEdge.SourceNodeId/TargetNodeId -> GraphNode (Restrict) -- another same-transaction
        // sibling-cascade hazard: GraphEdge and GraphNode both cascade from Project independently.
        db.GraphEdges.Add(new GraphEdge
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceNodeId = graphNodeA.Id, TargetNodeId = graphNodeB.Id,
            Kind = GraphEdgeKind.Contains
        });

        db.ClaudeIntegrations.Add(new ClaudeIntegration
        {
            ProjectId = project.Id, HookVersion = "1", McpConfigHash = "hash", InstalledAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();

        var repository = new ProjectRepository(db);
        var result = await repository.DeleteAsync(project.Id, CancellationToken.None);

        result.Should().BeTrue();

        // Every row the deleted project owned, transitively, is gone.
        (await db.Projects.AsNoTracking().AnyAsync(p => p.Id == project.Id)).Should().BeFalse();
        (await db.Files.AsNoTracking().AnyAsync(f => f.Id == file.Id)).Should().BeFalse();
        (await db.Symbols.AsNoTracking().AnyAsync(s => s.Id == symbol.Id)).Should().BeFalse();
        (await db.Symbols.AsNoTracking().AnyAsync(s => s.Id == childSymbol.Id)).Should().BeFalse();
        (await db.Chunks.AsNoTracking().AnyAsync(c => c.Id == chunk.Id)).Should().BeFalse();
        (await db.Sessions.AsNoTracking().AnyAsync(s => s.Id == session.Id)).Should().BeFalse();
        (await db.SessionEvents.AsNoTracking().AnyAsync(e => e.SessionId == session.Id)).Should().BeFalse();
        (await db.Memories.AsNoTracking().AnyAsync(m => m.Id == memory.Id)).Should().BeFalse();
        (await db.Memories.AsNoTracking().AnyAsync(m => m.Id == supersededMemory.Id)).Should().BeFalse();
        (await db.GraphNodes.AsNoTracking().AnyAsync(n => n.Id == graphNodeA.Id || n.Id == graphNodeB.Id)).Should().BeFalse();
        (await db.ClaudeIntegrations.AsNoTracking().AnyAsync(c => c.ProjectId == project.Id)).Should().BeFalse();

        // The other project and its own data are untouched.
        (await db.Projects.AsNoTracking().AnyAsync(p => p.Id == otherProject.Id)).Should().BeTrue();
        (await db.Files.AsNoTracking().AnyAsync(f => f.Id == otherFile.Id)).Should().BeTrue();

        // The shared Embeddings cache row survives -- deleting a project must never touch it.
        (await db.Embeddings.AsNoTracking().AnyAsync(e => e.ContentHash == "shared-hash" && e.Provider == "noop")).Should().BeTrue();
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by connection string, so the underlying OS file
        // handle can outlive the DbContext's own Dispose() -- clear the pool first or the file
        // deletes below intermittently throw "process cannot access the file".
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
