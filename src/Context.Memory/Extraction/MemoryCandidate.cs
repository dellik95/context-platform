using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>A proposed memory, before dedup/embedding/persistence.</summary>
/// <param name="Facts">Atomic, standalone facts extracted alongside the narrative <paramref name="Body"/> —
/// the compact form shown when a search result needs to say something useful in few tokens.</param>
internal sealed record MemoryCandidate(MemoryType Type, string Title, string Body, double Confidence, IReadOnlyList<string> Facts);
