using Context.Domain.Enums;

namespace Context.Retrieval.Abstractions;

/// <summary>
/// Persistence-facing seam for candidate retrieval. Context.Retrieval does not reference
/// Context.Infrastructure (see CONTRACTS.md dependency graph); Context.Application implements
/// this interface against Context.Infrastructure + SQLite FTS5, wiring the real BM25 lexical
/// query and the vector-similarity scan. Context.Retrieval only consumes the results — the
/// scoring/merge/rerank/budget logic lives here.
/// </summary>
public interface ISearchIndex
{
    /// <summary>
    /// SQLite FTS5 / BM25 lexical search scoped to a project. Each result identifies a chunk and,
    /// where resolvable, the symbol/file it belongs to (<c>SymbolId</c> and/or <c>FileId</c> may be
    /// null for chunks that are not attached to a resolved symbol). <c>SymbolKind</c> is the
    /// resolved symbol's actual kind (Class, Method, Function, ...) when <c>SymbolId</c> is set, so
    /// callers can report what a hit actually is instead of the generic "it's a symbol" — null when
    /// <c>SymbolId</c> is null.
    /// </summary>
    /// <param name="scope">
    /// Optional path/language restriction from <see cref="SearchQuery.Scope"/>: a chunk qualifies
    /// when any entry is a case-insensitive substring of its file's relative path or an exact
    /// match for its language. Null or empty means the whole project.
    /// </param>
    Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double BM25Score)>> LexicalSearchAsync(
        Guid projectId, string text, IReadOnlyList<string>? scope, CancellationToken ct);

    /// <summary>
    /// Vector-similarity search scoped to a project, using a pre-computed query embedding.
    /// <c>CosineScore</c> is the cosine similarity between the query embedding and the chunk's
    /// stored embedding, typically in [-1, 1]. <c>SymbolKind</c> is the resolved symbol's actual
    /// kind — see <see cref="LexicalSearchAsync"/>'s remarks.
    /// </summary>
    /// <param name="scope">
    /// Same path/language restriction as <see cref="LexicalSearchAsync"/>. The ANN index stores no
    /// path metadata, so implementations can only apply this <em>after</em> the top-K cut: a scoped
    /// vector search returns the in-scope subset of the project-wide nearest neighbors, not the
    /// nearest neighbors within the scope.
    /// </param>
    Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double CosineScore)>> VectorSearchAsync(
        Guid projectId, float[] queryEmbedding, IReadOnlyList<string>? scope, CancellationToken ct);
}
