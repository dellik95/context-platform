namespace Context.Memory.Abstractions;

/// <summary>The memory the candidate ended up in (new, reinforced, merged or the winning side of a
/// conflict) and the branch that produced it.</summary>
public sealed record ConsolidationResult(Context.Domain.Entities.Memory Memory, ConsolidationOutcome Outcome);
