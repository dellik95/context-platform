using System.Data.Common;
using System.Text;
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
    IProviderResolver providerResolver) : ISearchIndex, IDisposable
{
    private const int LexicalLimit = 50;
    private const int VectorLimit = 50;
    private const int SnippetLength = 400;
    private const int MaxScopeTerms = 16;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "these", "those",
        "what", "when", "where", "which", "who", "whom", "whose", "why", "how",
        "are", "was", "were", "been", "being", "have", "has", "had", "does", "did",
        "can", "could", "would", "should", "will", "shall", "may", "might", "must",
        "into", "onto", "about", "after", "before", "between", "under", "over",
        "find", "show", "give", "list", "get", "all", "any", "not", "use", "using"
    };

    // Instance-level, not static: SqliteSearchIndex is a singleton per DI container in production
    // (one instance == one database file, so this is still effectively "once per process" there),
    // but tests build more than one DI container -- each against its own temp database file --
    // within the same test process. A `static` flag here previously meant the FIRST test to touch
    // lexical search left every later test's *different* database permanently un-initialized
    // (chunks_fts never created for it), silently returning zero hits regardless of what data it
    // indexed -- indistinguishable from an actual search bug.
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _ftsInitialized;

    public void Dispose() => _initGate.Dispose();

    public async Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double BM25Score, string? Label, string? RelativePath)>> LexicalSearchAsync(
        Guid projectId, string text, IReadOnlyList<string>? scope, CancellationToken ct)
    {
        var allTerms = ExtractSearchTerms(text);
        var matchExpression = BuildMatchExpressionFromTerms(allTerms);
        var scopeTerms = NormalizeScope(scope);
        var preferNonTest = ShouldDownrankTests(allTerms, scopeTerms);

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

        // FTS5 trigram cannot index terms shorter than 3 characters (e.g. "id", "db", "ui").
        // Fall back to a bounded parameterized LIKE search when no >=3-char token existed or FTS5 yielded few hits.
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
                var snippet = ExtractSnippet(row.FullText, allTerms);
                var label = string.IsNullOrWhiteSpace(row.QualifiedName) ? row.RelativePath : row.QualifiedName;
                var adjusted = AdjustLexicalScore(row.BaseScore, allTerms, label, row.RelativePath, preferNonTest);
                return (row.ChunkId, row.FileId, row.SymbolId, row.SymbolKind, snippet, adjusted, (string?)label, (string?)row.RelativePath);
            })
            .OrderByDescending(r => r.adjusted)
            .Take(LexicalLimit)];
    }

    private static async Task<List<RawLexicalRow>> RunFtsQueryAsync(
        DbConnection connection, Guid projectId, string matchExpression, IReadOnlyList<string> scopeTerms, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // bm25() in FTS5 is "lower/more negative is better"; negate it so, like CosineScore,
        // a higher value here always means "more relevant".
        command.CommandText = $"""
            SELECT c."Id", c."FileId", c."SymbolId", sy."Kind", c."Text", -bm25(chunks_fts) AS Score, sy."QualifiedName", f."RelativePath"
            FROM chunks_fts
            JOIN "Chunks" c ON c."rowid" = chunks_fts."rowid"
            JOIN "Files" f ON f."Id" = c."FileId"
            LEFT JOIN "Symbols" sy ON sy."Id" = c."SymbolId"
            WHERE chunks_fts MATCH @match AND f."ProjectId" = @projectId AND f."IsDeleted" = 0{BuildScopeSql(scopeTerms)}
            ORDER BY bm25(chunks_fts)
            LIMIT @limit;
            """;
        AddParameter(command, "@match", matchExpression);
        AddScopeParameters(command, scopeTerms);
        AddParameter(command, "@projectId", projectId.ToString().ToUpperInvariant());
        AddParameter(command, "@limit", LexicalLimit);

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
            WHERE f."ProjectId" = @projectId AND f."IsDeleted" = 0{BuildScopeSql(scopeTerms)}
              AND ({string.Join(" OR ", termClauses)})
            LIMIT @limit;
            """;
        for (var i = 0; i < shortTerms.Count; i++)
        {
            AddParameter(command, $"@st{i}", $"%{EscapeLike(shortTerms[i].ToLowerInvariant())}%");
        }

        AddScopeParameters(command, scopeTerms);
        AddParameter(command, "@projectId", projectId.ToString().ToUpperInvariant());
        AddParameter(command, "@limit", LexicalLimit);

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

        // A hit whose ChunkId no longer resolves here (its chunk was replaced by a reindex, or its
        // file got deleted, after the vector index last saw it) is silently dropped -- the vector
        // index has no proactive delete-on-reindex step (see SqliteChunkVectorIndex's remarks), so
        // this join is what keeps a stale hit from ever reaching a real result.
        var rows = await (
            from c in db.Chunks.AsNoTracking()
            join f in db.Files.AsNoTracking() on c.FileId equals f.Id
            join sy in db.Symbols.AsNoTracking() on c.SymbolId equals (Guid?)sy.Id into symbolJoin
            from sy in symbolJoin.DefaultIfEmpty()
            where chunkIds.Contains(c.Id) && f.ProjectId == projectId && !f.IsDeleted
            select new { c.Id, c.FileId, c.SymbolId, Kind = (SymbolKind?)sy.Kind, QualifiedName = sy.QualifiedName, c.Text, f.RelativePath, f.Language }
        ).ToListAsync(ct).ConfigureAwait(false);

        // Post-filter, unavoidably: the ANN index has no path metadata to filter on during the
        // search itself (see IChunkVectorIndex), so a scoped query gets the in-scope subset of the
        // project's nearest neighbors — documented on ISearchIndex.VectorSearchAsync.
        var scopeTerms = NormalizeScope(scope);
        var preferNonTest = ShouldDownrankTests([], scopeTerms);
        var rowsInScope = scopeTerms.Count == 0
            ? rows
            : [.. rows.Where(r => MatchesScope(r.RelativePath, r.Language, scopeTerms))];

        var scored = rowsInScope
            .Select(row =>
            {
                var snippet = row.Text.Length > SnippetLength ? row.Text[..SnippetLength] : row.Text;
                var label = string.IsNullOrWhiteSpace(row.QualifiedName) ? row.RelativePath : row.QualifiedName;
                var cosine = scoreByChunkId[row.Id];
                if (preferNonTest && IsTestPath(row.RelativePath))
                {
                    cosine *= 0.85;
                }

                return (row.Id, (Guid?)row.FileId, row.SymbolId, row.Kind, snippet, cosine, (string?)label, (string?)row.RelativePath);
            })
            .OrderByDescending(r => r.cosine)
            .ToList();

        return scored;
    }

    /// <summary>
    /// A <see cref="Context.Retrieval.Abstractions.SearchQuery.Scope"/> entry matches a file when it
    /// appears anywhere in the relative path or names the file's language exactly — both
    /// case-insensitively, and a file needs to match only one entry. One list covers both filters
    /// because a caller says "src/Auth" or "csharp" without wanting to declare which kind it is;
    /// the two namespaces do not realistically collide.
    /// </summary>
    private static IReadOnlyList<string> NormalizeScope(IReadOnlyList<string>? scope) =>
        scope is null
            ? []
            : [.. scope.Where(s => !string.IsNullOrWhiteSpace(s))
                       .Select(s => s.Trim().ToLowerInvariant())
                       .Distinct()
                       .Take(MaxScopeTerms)];

    private static string BuildScopeSql(IReadOnlyList<string> scopeTerms)
    {
        if (scopeTerms.Count == 0)
        {
            return string.Empty;
        }

        var clauses = scopeTerms.Select((_, i) =>
            $"""lower(f."RelativePath") LIKE @scopePath{i} ESCAPE '\' OR lower(f."Language") = @scopeTerm{i}""");

        return $" AND ({string.Join(" OR ", clauses)})";
    }

    private static void AddScopeParameters(DbCommand command, IReadOnlyList<string> scopeTerms)
    {
        for (var i = 0; i < scopeTerms.Count; i++)
        {
            AddParameter(command, $"@scopePath{i}", $"%{EscapeLike(scopeTerms[i])}%");
            AddParameter(command, $"@scopeTerm{i}", scopeTerms[i]);
        }
    }

    private static bool MatchesScope(string relativePath, string? language, IReadOnlyList<string> scopeTerms) =>
        scopeTerms.Any(term =>
            relativePath.Contains(term, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, term, StringComparison.OrdinalIgnoreCase));

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static IReadOnlyList<string> ExtractSearchTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var rawTokens = text
            .Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0);

        var expanded = new List<string>();
        foreach (var raw in rawTokens)
        {
            expanded.Add(raw);
            SplitCamelCase(raw, expanded);
        }

        var filtered = expanded
            .Where(t => !Stopwords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();

        if (filtered.Count == 0)
        {
            filtered = expanded
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        return filtered;
    }

    private static void SplitCamelCase(string token, List<string> target)
    {
        if (token.Length < 4)
        {
            return;
        }

        var start = 0;
        for (var i = 1; i < token.Length; i++)
        {
            var prev = token[i - 1];
            var curr = token[i];
            var boundary = (char.IsLower(prev) && char.IsUpper(curr))
                || (char.IsLetter(prev) && char.IsDigit(curr))
                || (char.IsDigit(prev) && char.IsLetter(curr))
                || (i + 1 < token.Length && char.IsUpper(prev) && char.IsUpper(curr) && char.IsLower(token[i + 1]));
            if (!boundary)
            {
                continue;
            }

            if (i - start >= 2)
            {
                target.Add(token[start..i]);
            }

            start = i;
        }

        if (start > 0 && token.Length - start >= 2)
        {
            target.Add(token[start..]);
        }
    }

    private static string? BuildMatchExpressionFromTerms(IReadOnlyList<string> terms)
    {
        var trigramTerms = terms.Where(t => t.Length >= 3).Take(24).ToList();
        if (trigramTerms.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < trigramTerms.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append('"').Append(trigramTerms[i].Replace("\"", "\"\"")).Append('"');
        }

        return sb.ToString();
    }

    private static string ExtractSnippet(string text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= SnippetLength)
        {
            return text;
        }

        var earliestMatch = -1;
        foreach (var term in terms)
        {
            var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (earliestMatch < 0 || idx < earliestMatch))
            {
                earliestMatch = idx;
            }
        }

        if (earliestMatch < 0 || earliestMatch < SnippetLength - 100)
        {
            return text[..SnippetLength];
        }

        var start = Math.Max(0, earliestMatch - 100);
        // Align start cleanly to a line/word boundary near start so snippet stays legible.
        var newline = text.IndexOf('\n', start, Math.Min(40, text.Length - start));
        if (newline >= start && newline < earliestMatch)
        {
            start = newline + 1;
        }

        var length = Math.Min(SnippetLength, text.Length - start);
        var slice = text.Substring(start, length);
        return start > 0 ? $"…{slice}" : slice;
    }

    private static double AdjustLexicalScore(
        double baseScore,
        IReadOnlyList<string> terms,
        string? label,
        string? relativePath,
        bool preferNonTest)
    {
        var score = Math.Max(0.01, baseScore);

        if (!string.IsNullOrWhiteSpace(label) && terms.Count > 0)
        {
            var dot = label.LastIndexOf('.');
            var shortName = dot >= 0 && dot + 1 < label.Length ? label[(dot + 1)..] : label;

            var hitsInLabel = 0;
            var exactSymbolMatch = false;
            foreach (var term in terms)
            {
                if (string.Equals(shortName, term, StringComparison.OrdinalIgnoreCase))
                {
                    exactSymbolMatch = true;
                    hitsInLabel++;
                }
                else if (label.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    hitsInLabel++;
                }
            }

            if (hitsInLabel > 0)
            {
                var ratio = (double)hitsInLabel / terms.Count;
                score = score * (1.0 + (0.45 * ratio)) + (exactSymbolMatch ? 1.0 : 0.25 * ratio);
            }
        }

        if (preferNonTest && IsTestPath(relativePath))
        {
            score *= 0.75;
        }

        return score;
    }

    private static bool ShouldDownrankTests(IReadOnlyList<string> terms, IReadOnlyList<string> scopeTerms)
    {
        if (scopeTerms.Any(IsTestPath))
        {
            return false;
        }

        foreach (var term in terms)
        {
            if (term.StartsWith("test", StringComparison.OrdinalIgnoreCase)
                || term.StartsWith("spec", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTestPath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        return relativePath.Contains("/test", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("\\test", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("test/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("Tests.", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains(".test.", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains(".spec.", StringComparison.OrdinalIgnoreCase);
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

    // Symbols."Kind" has no HasConversion<string>() in SymbolConfiguration, so EF persists the enum
    // as its default underlying INTEGER (ordinal) -- read it the same way here on the raw connection.
    private static SymbolKind? ReadNullableSymbolKind(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (SymbolKind)reader.GetInt64(ordinal);
}
