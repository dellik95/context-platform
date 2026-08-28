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

public sealed record EmbedChunkPayload(Guid ChunkId, string ContentHash, string Text);

public sealed record SummarizeSessionPayload(Guid SessionId);

public sealed record ExtractMemoryPayload(Guid SessionId);

/// <summary>Marker payload — <c>DecayMemory</c> applies globally across every project.</summary>
public sealed record DecayMemoryPayload;

/// <summary>Marker payload — <c>BackupDb</c> always backs up the single active database.</summary>
public sealed record BackupDbPayload;

public sealed record ResolveRelationsPayload(Guid ProjectId);
