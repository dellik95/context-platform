namespace Context.Memory.Abstractions;

/// <summary>The memory <see cref="IMemoryConsolidator.ConsolidateAsync"/> ended up with, plus which
/// of the three branches (<see cref="ConsolidationOutcome"/>) produced it.</summary>
public sealed record ConsolidationResult(Context.Domain.Entities.Memory Memory, ConsolidationOutcome Outcome);
