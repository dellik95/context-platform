using System.Data.Common;
using Context.Application.Abstractions;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Retrieval.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Application.Adapters;

/// <summary>
/// Adapts Context.Retrieval's persistence-facing <see cref="ISearchIndex"/> seam onto SQLite:
/// <see cref="LexicalSearchAsync"/> executes FTS5/BM25 and short-token queries against
/// <c>chunks_fts</c>, while <see cref="VectorSearchAsync"/> delegates ANN lookup to
/// <see cref="IChunkVectorIndex"/> and hydrates matching live chunks from SQLite.
/// </summary>
public sealed class SqliteSearchIndex(
    IDbContextFactory<ContextDbContext> dbContextFactory,
    IChunkVectorIndex vectorIndex,
    IProviderResolver providerResolver) : ISearchIndex, IDisposable
{
    private const int LexicalLimit = 50;
    private const int VectorLimit = 50;

    // Instance-level gate: production uses one DB file per DI container, while tests isolate temp DBs per container.
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _ftsInitialized;

    public void Dispose() => _initGate.Dispose();

    public async Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double BM25Score, string? Label, string? RelativePath)>> LexicalSearchAsync(
        Guid projectId, string text, IReadOnlyList<string>? scope, CancellationToken ct)
    {
        var allTerms = SqliteSearchQueryHelper.ExtractSearchTerms(text);
        var matchExpression = SqliteSearchQueryHelper.BuildMatchExpressionFromTerms(allTerms);
        var scopeTerms = SqliteSearchQueryHelper.NormalizeScope(scope);
        var preferNonTest = SqliteSearchQueryHelper.ShouldDownrankTests(allTerms, scopeTerms);

        if (matchExpression is null && allTerms.Count == 0)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureFtsInitializedAsync(db, ct).ConfigureAwait(false);

        var connection = db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct).ConfigureAwait(false);

        var rawHits = new List<RawLexicalRow>();
        if (matchExpression is not null)
        {
            rawHits.AddRange(await RunFtsQueryAsync(connection, projectId, matchExpression, scopeTerms, ct).ConfigureAwait(false));
        }

        // FTS5 trigram skips <3 char identifiers ("id", "db", "ui"); bounded LIKE fallback catches them.
        var shortTerms = allTerms.Where(t => t.Length < 3).Take(3).ToList();
        if ((matchExpression is null || rawHits.Count < 5) && shortTerms.Count > 0)
        {
            var seenIds = rawHits.Select(r => r.ChunkId).ToHashSet();
            foreach (var fallbackRow in await RunShortTokenFallbackAsync(connection, projectId, shortTerms, scopeTerms, ct).ConfigureAwait(false))
            {
                if (seenIds.Add(fallbackRow.ChunkId))
                {
                    rawHits.Add(fallbackRow);
                }
            }
        }

        return [.. rawHits
            .Select(row =>
            {
                var snippet = SqliteSearchQueryHelper.ExtractSnippet(row.FullText, allTerms);
                var label = string.IsNullOrWhiteSpace(row.QualifiedName) ? row.RelativePath : row.QualifiedName;
                var adjusted = SqliteSearchQueryHelper.AdjustLexicalScore(row.BaseScore, allTerms, label, row.RelativePath, preferNonTest);
                return (row.ChunkId, row.FileId, row.SymbolId, row.SymbolKind, snippet, adjusted, (string?)label, (string?)row.RelativePath);
            })
            .OrderByDescending(r => r.adjusted)
            .Take(LexicalLimit)];
    }

    public async Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double CosineScore, string? Label, string? RelativePath)>> VectorSearchAsync(
        Guid projectId, float[] queryEmbedding, IReadOnlyList<string>? scope, CancellationToken ct)
    {
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var hits = await vectorIndex.SearchAsync(projectId, embeddingGenerator.GetProviderModelKey(), queryEmbedding, VectorLimit, ct)
            .ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return [];
        }

        var scoreByChunkId = hits.ToDictionary(h => h.ChunkId, h => h.Score);

        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var chunkIds = hits.Select(h => h.ChunkId).ToArray();

        // Filter deleted files / stale chunk IDs via inner join to live Chunks/Files rows.
        var rows = await (
            from c in db.Chunks.AsNoTracking()
            join f in db.Files.AsNoTracking() on c.FileId equals f.Id
            join sy in db.Symbols.AsNoTracking() on c.SymbolId equals (Guid?)sy.Id into symbolJoin
            from sy in symbolJoin.DefaultIfEmpty()
            where chunkIds.Contains(c.Id) && f.ProjectId == projectId && !f.IsDeleted
            select new { c.Id, c.FileId, c.SymbolId, Kind = (SymbolKind?)sy.Kind, QualifiedName = sy.QualifiedName, c.Text, f.RelativePath, f.Language }
        ).ToListAsync(ct).ConfigureAwait(false);

        var scopeTerms = SqliteSearchQueryHelper.NormalizeScope(scope);
        var preferNonTest = SqliteSearchQueryHelper.ShouldDownrankTests([], scopeTerms);
        var rowsInScope = scopeTerms.Count == 0
            ? rows
            : [.. rows.Where(r => SqliteSearchQueryHelper.MatchesScope(r.RelativePath, r.Language, scopeTerms))];

        return [.. rowsInScope
            .Select(row =>
            {
                var snippet = row.Text.Length > SqliteSearchQueryHelper.SnippetLength
                    ? row.Text[..SqliteSearchQueryHelper.SnippetLength]
                    : row.Text;
                var label = string.IsNullOrWhiteSpace(row.QualifiedName) ? row.RelativePath : row.QualifiedName;
                var cosine = scoreByChunkId[row.Id];
                if (preferNonTest && SqliteSearchQueryHelper.IsTestPath(row.RelativePath))
                {
                    cosine *= 0.85;
                }

                return (row.Id, (Guid?)row.FileId, row.SymbolId, row.Kind, snippet, cosine, (string?)label, (string?)row.RelativePath);
            })
            .OrderByDescending(r => r.cosine)];
    }

    private static async Task<List<RawLexicalRow>> RunFtsQueryAsync(
        DbConnection connection, Guid projectId, string matchExpression, IReadOnlyList<string> scopeTerms, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c."Id", c."FileId", c."SymbolId", sy."Kind", c."Text", -bm25(chunks_fts) AS Score, sy."QualifiedName", f."RelativePath"
            FROM chunks_fts
            JOIN "Chunks" c ON c."rowid" = chunks_fts."rowid"
            JOIN "Files" f ON f."Id" = c."FileId"
            LEFT JOIN "Symbols" sy ON sy."Id" = c."SymbolId"
            WHERE chunks_fts MATCH @match AND f."ProjectId" = @projectId AND f."IsDeleted" = 0{SqliteSearchQueryHelper.BuildScopeSql(scopeTerms)}
            ORDER BY bm25(chunks_fts)
            LIMIT @limit;
            """;
        SqliteSearchQueryHelper.AddParameter(command, "@match", matchExpression);
        SqliteSearchQueryHelper.AddScopeParameters(command, scopeTerms);
        SqliteSearchQueryHelper.AddParameter(command, "@projectId", projectId.ToString().ToUpperInvariant());
        SqliteSearchQueryHelper.AddParameter(command, "@limit", LexicalLimit);

        return await ReadLexicalRowsAsync(command, ct).ConfigureAwait(false);
    }

    private static async Task<List<RawLexicalRow>> RunShortTokenFallbackAsync(
        DbConnection connection, Guid projectId, IReadOnlyList<string> shortTerms, IReadOnlyList<string> scopeTerms, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        var termClauses = shortTerms.Select((_, i) =>
            $"""(lower(c."Text") LIKE @st{i} ESCAPE '\' OR lower(COALESCE(sy."QualifiedName", '')) LIKE @st{i} ESCAPE '\')""");
        command.CommandText = $"""
            SELECT c."Id", c."FileId", c."SymbolId", sy."Kind", c."Text", 1.0 AS Score, sy."QualifiedName", f."RelativePath"
            FROM "Chunks" c
            JOIN "Files" f ON f."Id" = c."FileId"
            LEFT JOIN "Symbols" sy ON sy."Id" = c."SymbolId"
            WHERE f."ProjectId" = @projectId AND f."IsDeleted" = 0{SqliteSearchQueryHelper.BuildScopeSql(scopeTerms)}
              AND ({string.Join(" OR ", termClauses)})
            LIMIT @limit;
            """;
        for (var i = 0; i < shortTerms.Count; i++)
        {
            SqliteSearchQueryHelper.AddParameter(command, $"@st{i}", $"%{SqliteSearchQueryHelper.EscapeLike(shortTerms[i].ToLowerInvariant())}%");
        }

        SqliteSearchQueryHelper.AddScopeParameters(command, scopeTerms);
        SqliteSearchQueryHelper.AddParameter(command, "@projectId", projectId.ToString().ToUpperInvariant());
        SqliteSearchQueryHelper.AddParameter(command, "@limit", LexicalLimit);

        return await ReadLexicalRowsAsync(command, ct).ConfigureAwait(false);
    }

    private static async Task<List<RawLexicalRow>> ReadLexicalRowsAsync(DbCommand command, CancellationToken ct)
    {
        var rows = new List<RawLexicalRow>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new RawLexicalRow(
                    ReadGuid(reader, 0),
                    ReadNullableGuid(reader, 1),
                    ReadNullableGuid(reader, 2),
                    ReadNullableSymbolKind(reader, 3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7)));
            }
        }
        catch (DbException)
        {
            // Malformed FTS5 query degrades gracefully to empty hits.
            return [];
        }

        return rows;
    }

    private sealed record RawLexicalRow(
        Guid ChunkId,
        Guid? FileId,
        Guid? SymbolId,
        SymbolKind? SymbolKind,
        string FullText,
        double BaseScore,
        string? QualifiedName,
        string RelativePath);

    private async Task EnsureFtsInitializedAsync(ContextDbContext db, CancellationToken ct)
    {
        if (_ftsInitialized)
        {
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ftsInitialized)
            {
                return;
            }

            var connection = db.Database.GetDbConnection();
            await EnsureOpenAsync(connection, ct).ConfigureAwait(false);

            if (!await FtsTableExistsAsync(connection, ct).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(text, content='Chunks', content_rowid='rowid', tokenize='trigram');", ct)
                    .ConfigureAwait(false);

                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER IF NOT EXISTS chunks_fts_ai AFTER INSERT ON "Chunks" BEGIN
                      INSERT INTO chunks_fts(rowid, text) VALUES (new.rowid, new."Text");
                    END;
                    """, ct).ConfigureAwait(false);

                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER IF NOT EXISTS chunks_fts_ad AFTER DELETE ON "Chunks" BEGIN
                      INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES('delete', old.rowid, old."Text");
                    END;
                    """, ct).ConfigureAwait(false);

                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER IF NOT EXISTS chunks_fts_au AFTER UPDATE ON "Chunks" BEGIN
                      INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES('delete', old.rowid, old."Text");
                      INSERT INTO chunks_fts(rowid, text) VALUES (new.rowid, new."Text");
                    END;
                    """, ct).ConfigureAwait(false);

                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO chunks_fts(rowid, text) SELECT \"rowid\", \"Text\" FROM \"Chunks\";", ct)
                    .ConfigureAwait(false);
            }

            _ftsInitialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private static async Task<bool> FtsTableExistsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'chunks_fts';";
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long count && count > 0;
    }

    private static async Task EnsureOpenAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    // Symbols."Kind" is persisted as its underlying INTEGER ordinal in SymbolConfiguration.
    private static SymbolKind? ReadNullableSymbolKind(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (SymbolKind)reader.GetInt64(ordinal);
}
