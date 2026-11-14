using Context.Memory.Abstractions;
using Context.Memory.Extraction;

namespace Context.Memory.Consolidation;

/// <summary>
/// Validated memory candidate enriched with extracted entities, normalized topic key,
/// timestamps, embedding vector, and preliminary code-verification result.
/// </summary>
internal sealed record IncomingMemory(
    Guid ProjectId,
    Guid? SessionId,
    MemoryCandidate Candidate,
    IReadOnlyList<ExtractedEntity> Entities,
    string? Topic,
    string? TopicKey,
    DateTimeOffset Now,
    DateTimeOffset ObservedAt)
{
    public Abstractions.MemoryVector? Embedding { get; init; }
    public CodeCheck CodeCheck { get; init; } = CodeCheck.Unknown;
}
