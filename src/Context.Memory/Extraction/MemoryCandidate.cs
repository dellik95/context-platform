using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>A proposed memory, before consolidation.</summary>
/// <param name="Source">Where the candidate came from; drives its base confidence and whether it may
/// supersede an existing memory.</param>
/// <param name="Facts">Atomic standalone facts, the compact form of <paramref name="Body"/>.</param>
/// <param name="Concepts">Knowledge-category tags from a fixed taxonomy (e.g. "gotcha", "pattern").</param>
/// <param name="FilesRead">Files the memory is about that were only read.</param>
/// <param name="FilesModified">Files the memory is about that were changed.</param>
/// <param name="Topic">Short reusable subsystem phrase shared across sessions, unlike the specific
/// <paramref name="Title"/>. Null when omitted.</param>
/// <param name="ScopePath">Repo-relative directory the memory is scoped to.</param>
public sealed record MemoryCandidate(
    MemoryType Type,
    string Title,
    string Body,
    EvidenceSource Source,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> FilesRead,
    IReadOnlyList<string> FilesModified,
    string? Topic = null,
    string? ScopePath = null)
{
    /// <summary>Concrete names the producer called out (ticket keys, symbols, config keys, files);
    /// merged with the entities extracted from the text.</summary>
    public IReadOnlyList<string> Entities { get; init; } = [];

    /// <summary>When the claim was observed (the newest event it was extracted from, or when the
    /// compact summary was captured); null means now. Newer-versus-older decisions compare this, not
    /// when consolidation happened to run.</summary>
    public DateTimeOffset? ObservedAt { get; init; }
}
