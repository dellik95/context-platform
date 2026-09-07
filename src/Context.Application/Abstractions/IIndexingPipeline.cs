namespace Context.Application.Abstractions;

/// <summary>
/// Orchestrates the end-to-end indexing pipeline for a project: scan/hash -> parse ->
/// symbol-level diff -> persist Files/Symbols/Chunks -> enqueue <c>UpdateGraph</c> and
/// <c>EmbedChunk</c> jobs only for what actually changed. Not part of CONTRACTS.md (which has
/// no interface for this) — added per the Context.Application task brief §4. See
/// <see cref="Context.Application.Indexing.IndexingPipeline"/> for the default implementation.
/// </summary>
public interface IIndexingPipeline
{
    /// <summary>
    /// (Re)indexes a single file already known to belong to <paramref name="projectId"/>. Skips
    /// all work if the file's content hash is unchanged since the last index and the file is not
    /// otherwise marked deleted. If the file no longer exists on disk, it is treated as a delete
    /// (mirrors the <c>DeleteFile</c> job's behavior).
    /// </summary>
    Task IndexFileAsync(Guid projectId, string relativePath, CancellationToken ct);

    /// <summary>
    /// Same as <see cref="IndexFileAsync(Guid, string, CancellationToken)"/>, but when
    /// <paramref name="forceReprocess"/> is true the content-hash skip is bypassed and the file is
    /// reprocessed regardless — the per-file primitive <see cref="ReindexProjectAsync"/> uses for a
    /// <c>full</c> rebuild.
    /// </summary>
    Task IndexFileAsync(Guid projectId, string relativePath, bool forceReprocess, CancellationToken ct);

    /// <summary>
    /// Rescans the whole project tree via <c>IFileScanner</c>/<c>IIgnoreMatcher</c>, indexes every
    /// discovered file, and marks any previously-indexed file no longer found on disk as deleted.
    /// When <paramref name="full"/> is true, every file is reprocessed regardless of content hash
    /// (a full rebuild); otherwise unchanged files are skipped exactly as in
    /// <see cref="IndexFileAsync"/>.
    /// </summary>
    Task ReindexProjectAsync(Guid projectId, bool full, CancellationToken ct);
}
