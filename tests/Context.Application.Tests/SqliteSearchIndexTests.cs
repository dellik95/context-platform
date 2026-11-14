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

    private async Task<Guid> AddFileAsync(Guid projectId, string relativePath, string chunkText, string? language = null)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var fileId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        db.Files.Add(new DomainFile
        {
            Id = fileId,
            ProjectId = projectId,
            RelativePath = relativePath,
            Language = language,
            ContentHash = "h2",
            IsDeleted = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.Chunks.Add(new Chunk { Id = chunkId, FileId = fileId, Text = chunkText, TokenCount = 5, ContentHash = "h2" });
        await db.SaveChangesAsync();

        return chunkId;
    }

    [Fact]
    public async Task LexicalSearchAsync_finds_a_PascalCase_identifier_via_a_multi_word_natural_language_query()
    {
        var (projectId, chunkId) = await SeedAsync("AuthMiddleware.cs", "public class AuthMiddleware { }");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var results = await index.LexicalSearchAsync(projectId, "auth middleware", null, CancellationToken.None);

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

        var hitsForA = await index.LexicalSearchAsync(projectAId, "auth", null, CancellationToken.None);
        var hitsForB = await index.LexicalSearchAsync(projectBId, "auth", null, CancellationToken.None);

        hitsForA.Should().Contain(r => r.ChunkId == chunkAId);
        hitsForB.Should().BeEmpty("project B's own chunk doesn't mention 'auth', and project A's hit must not leak across the project boundary");
    }

    [Fact]
    public async Task LexicalSearchAsync_restricts_hits_to_a_path_scope_term()
    {
        var (projectId, srcChunkId) = await SeedAsync("src/AuthMiddleware.cs", "public class AuthMiddleware { }");
        var testChunkId = await AddFileAsync(projectId, "tests/AuthMiddlewareTests.cs", "public class AuthMiddlewareTests { }");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var unscoped = await index.LexicalSearchAsync(projectId, "auth middleware", null, CancellationToken.None);
        var scoped = await index.LexicalSearchAsync(projectId, "auth middleware", ["tests/"], CancellationToken.None);

        unscoped.Should().Contain(r => r.ChunkId == srcChunkId).And.Contain(r => r.ChunkId == testChunkId);
        scoped.Should().Contain(r => r.ChunkId == testChunkId);
        scoped.Should().NotContain(r => r.ChunkId == srcChunkId);
    }

    [Fact]
    public async Task LexicalSearchAsync_matches_a_scope_term_against_the_files_language()
    {
        var (projectId, csharpChunkId) = await SeedAsync("Auth.cs", "class AuthService { }");
        var tsChunkId = await AddFileAsync(projectId, "auth.ts", "class AuthService { }", language: "typescript");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var scoped = await index.LexicalSearchAsync(projectId, "auth service", ["typescript"], CancellationToken.None);

        scoped.Should().Contain(r => r.ChunkId == tsChunkId);
        scoped.Should().NotContain(r => r.ChunkId == csharpChunkId);
    }

    [Fact]
    public async Task LexicalSearchAsync_treats_a_LIKE_wildcard_in_a_scope_term_as_a_literal()
    {
        var (projectId, plainChunkId) = await SeedAsync("src/Auth.cs", "class AuthService { }");
        var literalChunkId = await AddFileAsync(projectId, "src/a%b/Auth.cs", "class AuthService { }");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var scoped = await index.LexicalSearchAsync(projectId, "auth service", ["a%b"], CancellationToken.None);

        scoped.Should().Contain(r => r.ChunkId == literalChunkId);
        scoped.Should().NotContain(r => r.ChunkId == plainChunkId,
            "an unescaped '%' would turn the scope term into a wildcard that matches every path");
    }

    [Fact]
    public async Task LexicalSearchAsync_finds_short_two_character_token_when_trigram_fts_has_no_matches()
    {
        var (projectId, matchingChunkId) = await SeedAsync("src/UserRecord.cs", "public Guid Id { get; init; }");
        await AddFileAsync(projectId, "src/Helper.cs", "public sealed class Helper { }");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var results = await index.LexicalSearchAsync(projectId, "Id", null, CancellationToken.None);

        results.Should().Contain(r => r.ChunkId == matchingChunkId);
    }

    [Fact]
    public async Task LexicalSearchAsync_splits_camelCase_query_windows_snippet_and_exposes_symbol_label()
    {
        await using var setupDb = await _factory.CreateDbContextAsync();
        await setupDb.Database.MigrateAsync();

        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var symbolId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var prefixPadding = new string('x', 360);
        var chunkText = $"{prefixPadding}\npublic bool ValidateToken() => true;\n{new string('y', 120)}";

        setupDb.Projects.Add(new Project { Id = projectId, Name = "p", RootPath = $"/p/{projectId:N}", CreatedAt = DateTimeOffset.UtcNow });
        setupDb.Files.Add(new DomainFile
        {
            Id = fileId,
            ProjectId = projectId,
            RelativePath = "src/Auth/TokenValidator.cs",
            ContentHash = "h1",
            IsDeleted = false,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        setupDb.Symbols.Add(new Symbol
        {
            Id = symbolId,
            FileId = fileId,
            Name = "ValidateToken",
            QualifiedName = "Auth.TokenValidator.ValidateToken",
            Kind = Context.Domain.Enums.SymbolKind.Method,
            StartLine = 10,
            EndLine = 12,
            SignatureHash = "s1",
            BodyHash = "b1"
        });
        setupDb.Chunks.Add(new Chunk
        {
            Id = chunkId,
            FileId = fileId,
            SymbolId = symbolId,
            Text = chunkText,
            TokenCount = 50,
            ContentHash = "h1"
        });
        await setupDb.SaveChangesAsync();

        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());
        var results = await index.LexicalSearchAsync(projectId, "validateToken", null, CancellationToken.None);

        var hit = results.Should().ContainSingle(r => r.ChunkId == chunkId).Which;
        hit.Label.Should().Be("Auth.TokenValidator.ValidateToken");
        hit.RelativePath.Should().Be("src/Auth/TokenValidator.cs");
        hit.Snippet.Should().Contain("ValidateToken", "snippet windowing should center around the matched identifier even past char 300");
    }

    [Fact]
    public async Task LexicalSearchAsync_prefers_production_source_over_test_file_for_unscoped_query()
    {
        var (projectId, testChunkId) = await SeedAsync("tests/OrderServiceTests.cs", "public class OrderServiceTests { void PlaceOrder() {} }");
        var srcChunkId = await AddFileAsync(projectId, "src/OrderService.cs", "public class OrderService { void PlaceOrder() {} }");
        var index = new SqliteSearchIndex(_factory, Mock.Of<IChunkVectorIndex>(), Mock.Of<IProviderResolver>());

        var unscoped = await index.LexicalSearchAsync(projectId, "PlaceOrder", null, CancellationToken.None);
        var testScoped = await index.LexicalSearchAsync(projectId, "PlaceOrder", ["tests/"], CancellationToken.None);

        unscoped.Should().HaveCountGreaterThanOrEqualTo(2);
        unscoped[0].ChunkId.Should().Be(srcChunkId);
        testScoped.Should().Contain(r => r.ChunkId == testChunkId);
        testScoped.Should().NotContain(r => r.ChunkId == srcChunkId);
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
