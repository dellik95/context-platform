using Context.Domain.Enums;
using Context.Memory.Extraction;

namespace Context.Memory.Consolidation;

/// <summary>How a candidate relates to one existing memory. The numeric value is the priority used
/// to pick which verdict acts when a candidate relates to several neighbours.</summary>
public enum MemoryRelation
{
    Unrelated = 0,
    Complements = 1,
    Contradicts = 2,
    Supersedes = 3,
    Duplicate = 4,
}

public sealed record RelationVerdict(MemoryRelation Relation, string? Reason)
{
    public static RelationVerdict Unrelated { get; } = new(MemoryRelation.Unrelated, null);
}

/// <summary>The parts of a memory or candidate the relation judge looks at.</summary>
public sealed record RelationSubject(MemoryType Type, string Title, IReadOnlyList<string> Facts, string Body)
{
    public static RelationSubject From(Context.Domain.Entities.Memory memory) =>
        new(memory.Type, memory.Title, EntityExtractor.ParseList(memory.FactsJson), memory.Body);

    public static RelationSubject From(MemoryCandidate candidate) =>
        new(candidate.Type, candidate.Title, candidate.Facts, candidate.Body);
}

/// <summary>Cheap signals the offline judge decides from.</summary>
/// <param name="Cosine">Embedding cosine similarity, 0 when the vectors are not comparable.</param>
/// <param name="SharesStrongEntity">At least one ticket/symbol/config/file key in common.</param>
public sealed record RelationSignals(
    double Cosine,
    bool SharesStrongEntity,
    bool SameType,
    bool SameTopicKey,
    bool NegationAsymmetry,
    double ContentJaccard = 0.0,
    int SharedStrongEntityCount = 0);
