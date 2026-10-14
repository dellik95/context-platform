namespace Context.Domain.Enums;

/// <summary>
/// Values are explicit and permanent: the column is an int, so renumbering would silently
/// reinterpret every already-queued row. 1, 2 and 11 are retired and must not be reused.
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
    ReconsolidateMemory = 12,
    ProcessHook = 13,
    RememberMemory = 14,
    ForgetMemory = 15,
    ReindexProject = 16,
    RepairVectors = 17,
}
