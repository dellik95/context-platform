namespace Context.Memory.Extraction;

/// <param name="DiscardedBlocks">Blocks that carried content but named neither a type nor a title,
/// so they could not become candidates — a non-zero count means the model drifted from the
/// requested format and memories were lost.</param>
internal sealed record MemoryCandidateParseResult(
    IReadOnlyList<MemoryCandidate> Candidates,
    int DiscardedBlocks);
