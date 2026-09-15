using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>A proposed memory, before dedup/embedding/persistence.</summary>
/// <param name="Facts">Atomic, standalone facts extracted alongside the narrative <paramref name="Body"/> —
/// the compact form shown when a search result needs to say something useful in few tokens.</param>
/// <param name="Topic">Short, reusable phrase naming the feature/subsystem/component this memory is
/// about (see <see cref="MemoryExtractionPromptBuilder"/>'s TOPIC documentation) — distinct from
/// <paramref name="Title"/>, which is specific to this one memory, so that memories about the same
/// subject extracted across different sessions share the same Topic. Null when the LLM omitted it.</param>
/// <param name="Concepts">Knowledge-category tags drawn from a fixed taxonomy (see
/// <see cref="MemoryExtractionPromptBuilder"/>'s CONCEPTS documentation) — e.g. "gotcha", "pattern".</param>
/// <param name="FilesRead">Files this specific memory is about but that weren't modified (read for context/investigation).</param>
/// <param name="FilesModified">Files this specific memory is about that were actually changed.</param>
/// <param name="ScopePath">Repo-relative directory/module path this memory is scoped to (see
/// <see cref="Context.Domain.Entities.Memory.ScopePath"/>) — structured and hierarchical, unlike
/// <paramref name="Topic"/> (a short human-readable phrase). Null when the LLM omitted it or a
/// heuristic couldn't derive one.</param>
public sealed record MemoryCandidate(
    MemoryType Type,
    string Title,
    string Body,
    double Confidence,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> FilesRead,
    IReadOnlyList<string> FilesModified,
    string? Topic = null,
    string? ScopePath = null);
