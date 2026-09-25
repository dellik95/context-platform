namespace Context.Application.Jobs;

// Payload DTOs serialized into Job.PayloadJson via System.Text.Json. One record per JobType;
// types whose handler needs no input (DecayMemory, BackupDb) get an empty marker record.

public sealed record IndexFilePayload(Guid ProjectId, string RelativePath);

/// <summary>
/// Drives <see cref="Context.Application.Jobs.Handlers.UpdateGraphJobHandler"/>. When
/// <see cref="Invalidate"/> is true the handler only removes the file's owned graph nodes/edges
/// (used for deletes); otherwise it rebuilds them from the file's current Symbols.
/// </summary>
/// <param name="RelationDraftsJson">
/// JSON-serialized <c>IReadOnlyList&lt;Context.Indexing.Abstractions.RelationDraft&gt;</c>, produced
/// by <c>IndexingPipeline.IndexParsedFileAsync</c> from the same in-memory source text it already
/// parsed for symbol extraction. Lets <c>UpdateGraphJobHandler</c> skip re-reading the file from disk
/// and re-parsing it; null for jobs enqueued before this field existed, or when extraction failed, in
/// which case the handler falls back to its old re-parse-from-disk path.
/// </param>
public sealed record UpdateGraphPayload(Guid ProjectId, Guid FileId, bool Invalidate, string? RelationDraftsJson = null);

/// <summary>
/// One job per distinct content hash needing embedding (dedup: two chunks with byte-identical
/// content share one embedding-provider call), but <see cref="ChunkIds"/> lists every chunk that
/// content hash covers -- each of them needs its own record in the per-chunk vector index
/// (<see cref="Abstractions.IChunkVectorIndex"/>) even though they share one
/// <see cref="Context.Domain.Entities.Embedding"/> cache row.
/// </summary>
public sealed record EmbedChunkPayload(Guid ProjectId, IReadOnlyList<Guid> ChunkIds, string ContentHash, string Text);

/// <summary>
/// <see cref="IsSessionEnd"/> distinguishes <c>ContextEngine.EndSessionAsync</c>'s final summary
/// (true) from <see cref="Scheduling.SessionSummaryScheduler"/>'s periodic mid-session ones
/// (false) — <see cref="Handlers.SummarizeSessionJobHandler"/> uses it to bypass its
/// event-count gate, since a session that just ended won't get another pass to pick up whatever
/// the gate would have deferred.
/// </summary>
public sealed record SummarizeSessionPayload(Guid SessionId, bool IsSessionEnd = false);

/// <summary>
/// Exactly one source is set: <see cref="EventBatchId"/> (a batch of raw SessionEvents claimed by
/// <c>ContextEngine.RecordEventAsync</c>/<c>EndSessionAsync</c>), <see cref="CompactSummary"/> (the
/// text Claude Code produced on /compact), or the legacy <see cref="SummaryChunkId"/> that jobs
/// queued before event batching still carry.
/// </summary>
public sealed record ExtractMemoryPayload(
    Guid SessionId,
    Guid? SummaryChunkId = null,
    Guid? EventBatchId = null,
    string? CompactSummary = null);

/// <summary>Marker payload — <c>DecayMemory</c> applies globally across every project.</summary>
public sealed record DecayMemoryPayload;

/// <summary>Marker payload — <c>BackupDb</c> always backs up the single active database.</summary>
public sealed record BackupDbPayload;

public sealed record ResolveRelationsPayload(Guid ProjectId);

/// <summary>
/// Drives <see cref="Handlers.RecreateEmbeddingsJobHandler"/>: force-regenerates every chunk's and
/// memory's embedding for <see cref="ProjectId"/> under whatever provider/model
/// <see cref="Context.Providers.Abstractions.IProviderResolver"/> currently resolves — used after
/// switching embedding provider/model so the new provider's vector-index collection gets populated
/// in bulk instead of only filling in lazily as files happen to get re-touched.
/// </summary>
public sealed record RecreateEmbeddingsPayload(Guid ProjectId);

/// <summary>
/// Drives <see cref="Handlers.CompactMemoryJobHandler"/>: clusters and merges near-duplicate
/// memories for <see cref="ProjectId"/>. Per-project (unlike <see cref="DecayMemoryPayload"/>'s
/// host-wide marker) since compaction's interval/threshold are per-project
/// <see cref="Context.Application.Settings.ProjectSettingsDto"/> settings, fired by
/// <see cref="Context.Application.Scheduling.MemoryCompactionScheduler"/>.
/// </summary>
public sealed record CompactMemoryPayload(Guid ProjectId);
