namespace Context.Memory.Abstractions;

/// <summary>What <see cref="IMemoryConsolidator.ConsolidateAsync"/> did with one candidate.</summary>
public enum ConsolidationOutcome
{
    Created,
    Reinforced,
    Merged,
    Superseded,
    Contested,
}
