using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

internal sealed record ReconsolidationItem(
    DomainMemory Memory,
    HashSet<string> Strong,
    string? TopicKey,
    float[]? Vector,
    IReadOnlyList<string> Facts);
