namespace Context.Memory.Consolidation;

internal sealed record ReconsolidationGroupPlan(
    IReadOnlyList<ReconsolidationCanonical> Canonicals,
    IReadOnlyList<(ReconsolidationItem A, ReconsolidationItem B, string? Reason)> Contradictions,
    IReadOnlyList<Guid> KeepSeparate,
    bool Ambiguous,
    string? Note);
