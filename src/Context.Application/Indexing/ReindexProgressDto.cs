namespace Context.Application.Indexing;

// Fire-and-forget progress channel from IndexingPipeline to whatever wants to observe a
// reindex live (Context.Dashboard.Server wires a SignalR-backed implementation; default/fallback is a
// no-op so Context.Application has zero hard dependency on SignalR).
//
// FilesFailed counts files that threw during indexing this pass (see
// IndexingPipeline.ReindexProjectAsync's per-file try/catch) and were skipped rather than
// aborting the whole pass; it is always 0 until the pass completes, since failures can only be
// tallied once every file has been attempted.
//
// Cancelled / Failed are only set on the synthetic final event ReindexRunner sends when a pass
// observes cancellation (POST /api/projects/{id}/index/cancel) or throws -- IndexingPipeline itself
// never sets them. Completed is also true on that event: the reindex has ended even though the scan
// didn't finish, and Completed is what the Dashboard's progress UI keys off to stop showing
// "reindexing...".
public sealed record ReindexProgressDto(
    Guid ProjectId,
    int FilesProcessed,
    int FilesTotal,
    bool Completed,
    int FilesFailed = 0,
    bool Cancelled = false,
    bool Failed = false);
