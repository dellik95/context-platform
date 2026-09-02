namespace Context.Application.Indexing;

// Fire-and-forget progress channel from IndexingPipeline to whatever wants to observe a
// reindex live (Context.Host wires a SignalR-backed implementation; default/fallback is a
// no-op so Context.Application has zero hard dependency on SignalR).
//
// FilesFailed counts files that threw during indexing this pass (see
// IndexingPipeline.ReindexProjectAsync's per-file try/catch) and were skipped rather than
// aborting the whole pass; it is always 0 until the pass completes, since failures can only be
// tallied once every file has been attempted.
public sealed record ReindexProgressDto(Guid ProjectId, int FilesProcessed, int FilesTotal, bool Completed, int FilesFailed = 0);
