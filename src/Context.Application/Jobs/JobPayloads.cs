namespace Context.Application.Jobs;

// Payload DTOs serialized into Job.PayloadJson (CONTRACTS.md §1) via System.Text.Json.
// One record per JobType that actually needs structured input; JobType values whose handler
// needs no input (DecayMemory, BackupDb) get an empty marker record for symmetry/clarity.

public sealed record IndexFilePayload(Guid ProjectId, string RelativePath);

public sealed record DeleteFilePayload(Guid ProjectId, string RelativePath);

/// <summary>
/// Drives <see cref="Context.Application.Jobs.Handlers.UpdateGraphJobHandler"/>. When
/// <see cref="Invalidate"/> is true the handler only removes the file's owned graph nodes/edges
/// (used for deletes); otherwise it rebuilds them from the file's current Symbols.
/// </summary>
public sealed record UpdateGraphPayload(Guid ProjectId, Guid FileId, bool Invalidate);

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
/// event-count gate before chaining into <c>ExtractMemory</c>, since a session that just ended
/// won't get another pass to pick up whatever the gate would have deferred.
/// </summary>
public sealed record SummarizeSessionPayload(Guid SessionId, bool IsSessionEnd = false);

/// <summary><see cref="SummaryChunkId"/> is the single <see cref="Context.Domain.Entities.SessionSummaryChunk"/>
/// this pass extracts memories from — not the session's full merged history.</summary>
public sealed record ExtractMemoryPayload(Guid SessionId, Guid SummaryChunkId);

/// <summary>Marker payload — <c>DecayMemory</c> applies globally across every project.</summary>
public sealed record DecayMemoryPayload;

/// <summary>Marker payload — <c>BackupDb</c> always backs up the single active database.</summary>
public sealed record BackupDbPayload;

public sealed record ResolveRelationsPayload(Guid ProjectId);
