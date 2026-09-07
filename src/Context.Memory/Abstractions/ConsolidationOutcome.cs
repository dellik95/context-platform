namespace Context.Memory.Abstractions;

/// <summary>Which branch <see cref="IMemoryConsolidator.ConsolidateAsync"/> took for one candidate —
/// exists so callers can report structured counts ("N created, M reinforced, K superseded",
/// docs/architecture/MEMORY_PIPELINE_PLAN.md §D.5/Gap #5) without re-deriving it from the returned
/// <see cref="Context.Domain.Entities.Memory"/> alone, which look identical across branches from the
/// outside (a superseding insert and a fresh insert are both "a new Memory row").</summary>
public enum ConsolidationOutcome
{
    Created,
    Reinforced,
    Superseded
}
