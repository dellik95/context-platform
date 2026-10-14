using Context.Domain.Enums;

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

/// <summary>A batch of content to embed, sent to the provider in as few requests as its batch limits allow.</summary>
public sealed record EmbedChunkPayload(Guid ProjectId, IReadOnlyList<EmbedChunkItem> Items);

/// <summary>
/// One distinct content hash (two chunks with byte-identical content share one embedding), with
/// every chunk it covers: each needs its own record in the per-chunk vector index
/// (<see cref="Abstractions.IChunkVectorIndex"/>) even though they share one
/// <see cref="Context.Domain.Entities.Embedding"/> cache row.
/// </summary>
public sealed record EmbedChunkItem(string ContentHash, string Text, IReadOnlyList<Guid> ChunkIds);

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
/// <c>ContextEngine.RecordEventAsync</c>/<c>EndSessionAsync</c>) or <see cref="CompactSummary"/> (the
/// text Claude Code produced on /compact).
/// </summary>
public sealed record ExtractMemoryPayload(
    Guid SessionId,
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
/// Drives <see cref="Handlers.ReconsolidateMemoryJobHandler"/>. A dry run only plans and stores a
/// Pending report; with <see cref="ReportId"/> set, that stored report is applied instead of planning
/// anew; otherwise the plan is applied straight away (the scheduled path).
/// </summary>
public sealed record ReconsolidateMemoryPayload(
    Guid ProjectId,
    bool DryRun = true,
    Guid? ReportId = null,
    IReadOnlyList<int>? ExcludeGroups = null);

/// <summary>
/// Drives <see cref="Handlers.RememberMemoryJobHandler"/>: the arguments of an explicit
/// <c>memory_remember</c>, stored before any embedding or LLM call so the content survives a provider outage.
/// </summary>
public sealed record RememberMemoryPayload(
    string Content,
    MemoryType? Type = null,
    string? Title = null,
    string? Topic = null,
    IReadOnlyList<string>? Facts = null,
    IReadOnlyList<string>? Concepts = null,
    IReadOnlyList<string>? FilesRead = null,
    IReadOnlyList<string>? FilesModified = null);

/// <summary>Drives <see cref="Handlers.ForgetMemoryJobHandler"/>.</summary>
public sealed record ForgetMemoryPayload(Guid MemoryId);

/// <summary>
/// Drives <see cref="Handlers.ProcessHookJobHandler"/>: one Claude Code hook call, captured as the
/// request path's only durable write. <see cref="InputJson"/> is the hook's JSON exactly as the
/// matching <c>Context.Claude.Hooks</c> input record serializes it; <see cref="ReceivedAt"/> is the
/// instant the host received it, which orders the resulting event however late the job runs.
/// </summary>
public sealed record ProcessHookPayload(HookType Type, string InputJson, DateTimeOffset ReceivedAt);

/// <summary>Drives <see cref="Handlers.ReindexProjectJobHandler"/>: a full or incremental pass over the project's tree.</summary>
public sealed record ReindexProjectPayload(Guid ProjectId, bool Full);


/// <summary>Drives <see cref="Handlers.RepairVectorsJobHandler"/>: reconciles a project's vector indexes with its database.</summary>
public sealed record RepairVectorsPayload(Guid ProjectId);
