using System.Data.Common;
using System.Text;
using Context.Application.Abstractions;
using Context.Infrastructure;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Retrieval.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Application.Adapters;

/// <summary>
/// Adapts Context.Retrieval's persistence-facing <see cref="ISearchIndex"/> seam onto SQLite:
/// <see cref="LexicalSearchAsync"/> is a real FTS5/BM25 query against a <c>chunks_fts</c> virtual
/// table kept in sync with the <c>Chunks</c> table by triggers (lazily created on first use — see
/// <see cref="EnsureFtsInitializedAsync"/>); <see cref="VectorSearchAsync"/> delegates the actual
/// nearest-neighbor search to <see cref="IChunkVectorIndex"/> (a real ANN index, sqlite-vec-backed
/// — see <see cref="SqliteChunkVectorIndex"/>) and then joins the hits back to <c>Chunks</c>/
/// <c>Files</c> for the file/symbol/snippet a search result needs.
/// </summary>
/// <remarks>
/// <b>Lifetime:</b> registered as a singleton because Context.Retrieval's <c>HybridSearchEngine</c>
/// (its consumer, via <c>AddContextRetrieval</c>) is itself a singleton. It therefore cannot
/// constructor-inject the pooled, Scoped <see cref="ContextDbContext"/> directly — DI would reject
/// a Scoped dependency captured by a singleton — so it opens a short-lived context per call via
/// <see cref="IDbContextFactory{TContext}"/> instead (registered alongside the pool by
/// <c>AddContextApplication</c>, targeting the same database file). See the identical note on
/// <see cref="GraphPersistenceAdapter"/>.
/// </remarks>
public sealed class SqliteSearchIndex(
    IDbContextFactory<ContextDbContext> dbContextFactory,
    IChunkVectorIndex vectorIndex,
    IProviderResolver providerResolver) : ISearchIndex
{
    private const int LexicalLimit = 50;
    private const int VectorLimit = 50;
    private const int SnippetLength = 400;

    // Instance-level, not static: SqliteSearchIndex is a singleton per DI container in production
    // (one instance == one database file, so this is still effectively "once per process" there),
    // but tests build more than one DI container -- each against its own temp database file --
    // within the same test process. A `static` flag here previously meant the FIRST test to touch
    // lexical search left every later test's *different* database permanently un-initialized
    // (chunks_fts never created for it), silently returning zero hits regardless of what data it
    // indexed -- indistinguishable from an actual search bug.
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _ftsInitialized;

    public async Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double BM25Score)>> LexicalSearchAsync(
        Guid projectId, string text, CancellationToken ct)
    {
        var matchExpression = BuildMatchExpression(text);
        if (matchExpression is null)
        {
            return [];
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await EnsureFtsInitializedAsync(db, ct).ConfigureAwait(false);

        var connection = db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        // bm25() in FTS5 is "lower/more negative is better"; negate it so, like CosineScore,
        // a higher value here always means "more relevant" — the convention HybridSearchEngine's
        // min-max normalization assumes for every raw signal it merges.
        command.CommandText = """
            SELECT c."Id", c."FileId", c."SymbolId", substr(c."Text", 1, @snippetLength) AS Snippet, -bm25(chunks_fts) AS Score
            FROM chunks_fts
            JOIN "Chunks" c ON c."rowid" = chunks_fts."rowid"
            JOIN "Files" f ON f."Id" = c."FileId"
            WHERE chunks_fts MATCH @match AND f."ProjectId" = @projectId AND f."IsDeleted" = 0
            ORDER BY bm25(chunks_fts)
            LIMIT @limit;
            """;
        AddParameter(command, "@match", matchExpression);
        // EF Core's Sqlite provider persists Guid columns as upper-invariant TEXT (confirmed
        // against this project's own on-disk data: e.g. Files."ProjectId" is stored as
        // "4FE55277-...", not "4fe55277-..."). This command runs on the raw ADO.NET connection,
        // bypassing EF's value conversion entirely, so binding the default (lowercase)
        // Guid.ToString() here made this WHERE clause compare against SQLite's case-sensitive
        // default (BINARY) collation and never match -- LexicalSearchAsync silently returned zero
        // rows for every project. Match EF's own on-disk casing so the comparison actually hits.
        AddParameter(command, "@projectId", projectId.ToString().ToUpperInvariant());
        AddParameter(command, "@snippetLength", SnippetLength);
        AddParameter(command, "@limit", LexicalLimit);

        var results = new List<(Guid, Guid?, Guid?, string, double)>();

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add((
                    ReadGuid(reader, 0),
                    ReadNullableGuid(reader, 1),
                    ReadNullableGuid(reader, 2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.GetDouble(4)));
            }
        }
        catch (DbException)
        {
            // A malformed FTS5 MATCH expression (pathological user input that slipped past
            // BuildMatchExpression's sanitizing) must degrade to "no lexical hits", never break
            // the whole hybrid search pipeline.
            return [];
        }

        return results;
    }

    public async Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double CosineScore)>> VectorSearchAsync(
        Guid projectId, float[] queryEmbedding, CancellationToken ct)
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

        // A hit whose ChunkId no longer resolves here (its chunk was replaced by a reindex, or its
        // file got deleted, after the vector index last saw it) is silently dropped -- the vector
        // index has no proactive delete-on-reindex step (see SqliteChunkVectorIndex's remarks), so
        // this join is what keeps a stale hit from ever reaching a real result.
        var rows = await (
            from c in db.Chunks.AsNoTracking()
            join f in db.Files.AsNoTracking() on c.FileId equals f.Id
            where chunkIds.Contains(c.Id) && f.ProjectId == projectId && !f.IsDeleted
            select new { c.Id, c.FileId, c.SymbolId, c.Text }
        ).ToListAsync(ct).ConfigureAwait(false);

        var scored = rows
            .Select(row =>
            {
                var snippet = row.Text.Length > SnippetLength ? row.Text[..SnippetLength] : row.Text;
                return (row.Id, (Guid?)row.FileId, row.SymbolId, snippet, scoreByChunkId[row.Id]);
            })
            .OrderByDescending(r => r.Item5)
            .ToList();

        return scored;
    }

    /// <summary>
    /// Turns free-form query text into a safe FTS5 MATCH expression: split into word tokens,
    /// each individually double-quoted (FTS5's quoting escapes it as a literal string token,
    /// sidestepping the query-syntax operators a raw query could otherwise trigger) and OR'd
    /// together. Returns null if the query has no usable tokens (e.g. empty/all-punctuation).
    /// </summary>
    private static string? BuildMatchExpression(string text)
    {
        var tokens = text
            .Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24) // cap query fan-out
            .ToList();

        if (tokens.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append('"').Append(tokens[i].Replace("\"", "\"\"")).Append('"');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Lazily creates the <c>chunks_fts</c> FTS5 virtual table and the triggers that keep it in
    /// sync with <c>Chunks</c> (insert/update/delete), then backfills it from any pre-existing
    /// rows — all guarded so it only ever runs once per process regardless of how many scoped
    /// <see cref="SqliteSearchIndex"/> instances get constructed, and idempotent across process
    /// restarts against the same on-disk database (checked via <c>sqlite_master</c>).
    /// </summary>
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

            var alreadyExists = await FtsTableExistsAsync(connection, ct).ConfigureAwait(false);
            if (!alreadyExists)
            {
                // trigram (not the FTS5 default, unicode61): unicode61 only splits on
                // non-alphanumeric characters, so a source-code identifier like "AuthMiddleware"
                // indexes as one token "authmiddleware" -- a query for "auth" or "middleware"
                // (BuildMatchExpression's OR-of-tokens, built from natural-language-style search
                // text) then never matches it, silently returning zero lexical hits for exactly the
                // kind of query this search exists for. trigram tokenizes into overlapping
                // 3-character sequences instead, which matches substrings regardless of
                // camelCase/PascalCase/snake_case word boundaries.
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
}
