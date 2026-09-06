using Context.Application.Abstractions;
using Context.Application.Adapters;
using Context.Domain.Entities;
using Context.Infrastructure;
using Context.Providers.Abstractions;
using Context.Retrieval.Abstractions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Tests;

/// <summary>
/// Regression coverage for two real bugs found while diagnosing "search returns nothing" for a
/// query like "auth middleware" against a codebase containing an `AuthMiddleware` class:
/// (1) <see cref="SqliteSearchIndex.LexicalSearchAsync"/> bound its `@projectId` parameter as
/// lowercase <c>Guid.ToString()</c> against a raw ADO.NET connection, but EF Core's Sqlite provider
/// persists Guid columns as UPPERCASE text -- confirmed against live production data (see
/// SqliteSearchIndex's own remarks) -- so the WHERE clause's case-sensitive default (BINARY)
/// collation never matched, for every project, always.
/// (2) `chunks_fts` used FTS5's default `unicode61` tokenizer, which only splits on
/// non-alphanumeric characters -- a PascalCase/camelCase identifier like `AuthMiddleware` indexes
/// as one token `authmiddleware`, so a natural-language multi-word query built from separate tokens
/// ("auth" OR "middleware") never matched it. Switched to the `trigram` tokenizer, which matches
/// substrings regardless of word-boundary conventions.
/// </summary>
public sealed class SqliteSearchIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-search-tests-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;
    private readonly IDbContextFactory<ContextDbContext> _factory;

    public SqliteSearchIndexTests()
    {
        var services = new ServiceCollection();
        services.AddPooledDbContextFactory<ContextDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _provider = services.BuildServiceProvider();
        _factory = _provider.GetRequiredService<IDbContextFactory<ContextDbContext>>();
    }

    private async Task<(Guid ProjectId, Guid ChunkId)> SeedAsync(string relativePath, string chunkText)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        db.Projects.Add(new Project { Id = projectId, Name = "p", RootPath = $"/p/{projectId:N}", CreatedAt = DateTimeOffset.UtcNow });
        db.Files.Add(new DomainFile
        {
            Id = fileId,
            ProjectId = projectId,
            RelativePath = relativePath,
            ContentHash = "h1",
            IsDeleted = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Chunks.Add(new Chunk { Id = chunkId, FileId = fileId, Text = chunkText, TokenCount = 5, ContentHash = "h1" });
        await db.SaveChangesAsync();

        return (projectId, chunkId);
    }

    [Fact]
    public async Task LexicalSearchAsync_finds_a_PascalCase_identifier_via_a_multi_word_natural_language_query()
    {
        var (projectId, chunkId) = await SeedAsync("AuthMiddleware.cs", "public class AuthMiddleware { }");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var results = await index.LexicalSearchAsync(projectId, "auth middleware", CancellationToken.None);

        results.Should().Contain(r => r.ChunkId == chunkId,
            "the query's separate words should match the identifier's substrings even though " +
            "'AuthMiddleware' tokenizes as one word under word-boundary tokenization");
    }

    [Fact]
    public async Task LexicalSearchAsync_only_returns_hits_scoped_to_the_requested_project()
    {
        var (projectAId, chunkAId) = await SeedAsync("AuthMiddleware.cs", "public class AuthMiddleware { }");
        var (projectBId, _) = await SeedAsync("Other.cs", "public class Unrelated { }");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var hitsForA = await index.LexicalSearchAsync(projectAId, "auth", CancellationToken.None);
        var hitsForB = await index.LexicalSearchAsync(projectBId, "auth", CancellationToken.None);

        hitsForA.Should().Contain(r => r.ChunkId == chunkAId);
        hitsForB.Should().BeEmpty("project B's own chunk doesn't mention 'auth', and project A's hit must not leak across the project boundary");
    }

    public void Dispose()
    {
        _provider.Dispose();
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
