using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>A proposed memory, before dedup/embedding/persistence.</summary>
internal sealed record MemoryCandidate(MemoryType Type, string Title, string Body, double Confidence);
