namespace Context.Domain.Enums;

/// <summary>
/// Values are explicit and permanent: the column is an int, so renumbering would silently
/// reinterpret every already-queued row. 1 (<c>DeleteFile</c>) and 2 (<c>ParseFile</c>) are retired
/// — both handlers did exactly what <c>IndexFile</c> already does for the same path
/// (<c>IndexingPipeline.IndexFileAsync</c> detects a missing file and tears the record down itself),
/// and nothing ever enqueued either one.
/// </summary>
public enum JobType
{
    IndexFile = 0,
    ResolveRelations = 3,
    UpdateGraph = 4,
    EmbedChunk = 5,
    SummarizeSession = 6,
    ExtractMemory = 7,
    DecayMemory = 8,
    BackupDb = 9,
    RecreateEmbeddings = 10,
    CompactMemory = 11,
}
