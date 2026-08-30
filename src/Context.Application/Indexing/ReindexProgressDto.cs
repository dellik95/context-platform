namespace Context.Application.Indexing;

// Fire-and-forget progress channel from IndexingPipeline to whatever wants to observe a
// reindex live (Context.Host wires a SignalR-backed implementation; default/fallback is a
// no-op so Context.Application has zero hard dependency on SignalR).
public sealed record ReindexProgressDto(Guid ProjectId, int FilesProcessed, int FilesTotal, bool Completed);
