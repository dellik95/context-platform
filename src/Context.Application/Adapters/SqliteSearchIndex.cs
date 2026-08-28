using System.Data.Common;
using System.Text;
using Context.Application.Support;
using Context.Infrastructure;
using Context.Retrieval.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Application.Adapters;

/// <summary>
/// Adapts Context.Retrieval's persistence-facing <see cref="ISearchIndex"/> seam onto SQLite:
/// <see cref="LexicalSearchAsync"/> is a real FTS5/BM25 query against a <c>chunks_fts</c> virtual
/// table kept in sync with the <c>Chunks</c> table by triggers (lazily created on first use — see
/// <see cref="EnsureFtsInitializedAsync"/>); <see cref="VectorSearchAsync"/> is a brute-force
/// cosine scan over every project chunk's stored <see cref="Domain.Entities.Embedding"/>, which is
/// perfectly adequate at the single-project, local-first scale this platform targets (see
/// CONTRACTS.md §9 task brief) but would need a real ANN index (e.g. sqlite-vec, or an external
/// vector store) past roughly tens of thousands of chunks per project — flagged as follow-up.
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
public sealed class SqliteSearchIndex(IDbContextFactory<ContextDbContext> dbContextFactory) : ISearchIndex
{
    private const int LexicalLimit = 50;
    private const int VectorLimit = 50;
    private const int SnippetLength = 400;

    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static volatile bool _ftsInitialized;

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
        AddParameter(command, "@projectId", projectId.ToString());
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
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await (
            from c in db.Chunks.AsNoTracking()
            join f in db.Files.AsNoTracking() on c.FileId equals f.Id
            join e in db.Embeddings.AsNoTracking() on c.ContentHash equals e.ContentHash
            where f.ProjectId == projectId && !f.IsDeleted
            select new { c.Id, c.FileId, c.SymbolId, c.Text, e.Vector }
        ).ToListAsync(ct).ConfigureAwait(false);

        var scored = new List<(Guid, Guid?, Guid?, string, double)>(rows.Count);
        foreach (var row in rows)
        {
            var vector = EmbeddingVectorCodec.Decode(row.Vector);
            var score = CosineSimilarity.Compute(queryEmbedding, vector);
            var snippet = row.Text.Length > SnippetLength ? row.Text[..SnippetLength] : row.Text;
            scored.Add((row.Id, row.FileId, row.SymbolId, snippet, score));
        }

        scored.Sort((a, b) => b.Item5.CompareTo(a.Item5));
        return scored.Count > VectorLimit ? scored.GetRange(0, VectorLimit) : scored;
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
    private static async Task EnsureFtsInitializedAsync(ContextDbContext db, CancellationToken ct)
    {
        if (_ftsInitialized)
        {
            return;
        }

        await InitGate.WaitAsync(ct).ConfigureAwait(false);
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
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(text, content='Chunks', content_rowid='rowid');", ct)
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
            InitGate.Release();
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
