using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

internal sealed record ConsolidationNeighbour(
    DomainMemory Memory,
    double Cosine,
    HashSet<string> StrongKeys,
    double Score);
