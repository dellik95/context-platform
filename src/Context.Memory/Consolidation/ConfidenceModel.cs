using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Memory.Consolidation;

/// <summary>
/// Confidence as a pure function of a memory's evidence: the most reliable supporting source, a small
/// bonus per additional independent source, the latest code check, and a penalty while contested.
/// </summary>
public static class ConfidenceModel
{
    public const double Min = 0.05;
    public const double Max = 0.95;
    public const double IndependentSourceBonus = 0.05;
    public const double MaxIndependentSourceBonus = 0.15;
    public const double VerifiedBonus = 0.05;
    public const double RefutedPenalty = 0.20;
    public const double ContestedPenalty = 0.25;
    public const double ExplicitRuleFloor = 0.85;

    private const double LegacyReliability = 0.65;

    public static double Reliability(EvidenceSource source) => source switch
    {
        EvidenceSource.Explicit => 0.90,
        EvidenceSource.Code => 0.90,
        EvidenceSource.CompactSummary => 0.75,
        EvidenceSource.EventBatch => 0.70,
        EvidenceSource.Consolidation => 0.70,
        EvidenceSource.Legacy => LegacyReliability,
        EvidenceSource.Heuristic => 0.40,
        _ => LegacyReliability,
    };

    public static double Compute(IReadOnlyCollection<MemoryEvidence> evidence, MemoryStatus status, MemoryType type)
    {
        var contested = status == MemoryStatus.Contested;
        var confidence = BestSourceReliability(evidence)
            + Math.Min(MaxIndependentSourceBonus, IndependentSourceBonus * (CountIndependentSources(evidence) - 1));

        // Only the latest code check counts: code changes, and an old refutation must not outlive a fix.
        var codeCheck = LatestCodeCheck(evidence);
        if (codeCheck == EvidenceRelation.Verified)
        {
            confidence += VerifiedBonus;
        }
        else if (codeCheck == EvidenceRelation.Refuted)
        {
            confidence -= RefutedPenalty;
        }

        if (contested)
        {
            confidence -= ContestedPenalty;
        }
        else if (type is MemoryType.Decision or MemoryType.Constraint
            && Supporting(evidence).Any(e => e.Source == EvidenceSource.Explicit))
        {
            confidence = Math.Max(confidence, ExplicitRuleFloor);
        }

        return Math.Clamp(confidence, Min, Max);
    }

    /// <summary>Distinct sessions (or explicit assertions, or source memories) behind the supporting
    /// evidence; repeats within one session count once. At least 1.</summary>
    public static int CountIndependentSources(IReadOnlyCollection<MemoryEvidence> evidence)
    {
        var supporting = Supporting(evidence).ToList();

        // A sessionless Merged link adds nothing when the source's own evidence was copied alongside it
        // (reconsolidation); otherwise an explicit-only source would count once per assertion plus once more.
        var copiedFrom = supporting
            .Where(e => e.Relation != EvidenceRelation.Merged && e.SourceMemoryId is not null)
            .Select(e => e.SourceMemoryId!.Value)
            .ToHashSet();

        var count = supporting
            .Where(e => !(e.Relation == EvidenceRelation.Merged
                && e.Source != EvidenceSource.Explicit
                && e.SessionId is null
                && e.SourceMemoryId is { } from
                && copiedFrom.Contains(from)))
            .Select(e => e.Source == EvidenceSource.Explicit
                ? $"explicit:{(object?)e.SessionId ?? e.Id}"
                : e.SessionId is { } sessionId ? $"session:{sessionId}"
                : e.SourceMemoryId is { } memoryId ? $"memory:{memoryId}"
                : $"source:{e.Source}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        return Math.Max(1, count);
    }

    /// <summary>Reliability of the strongest supporting source; memories with no evidence rank as legacy.</summary>
    public static double BestSourceReliability(IReadOnlyCollection<MemoryEvidence> evidence) =>
        Supporting(evidence).Select(e => Reliability(e.Source)).DefaultIfEmpty(LegacyReliability).Max();

    /// <summary>Verified or Refuted from the most recent code check; null when the code was never checked.</summary>
    public static EvidenceRelation? LatestCodeCheck(IEnumerable<MemoryEvidence> evidence) =>
        evidence
            .Where(e => e.Relation is EvidenceRelation.Verified or EvidenceRelation.Refuted)
            .OrderBy(e => e.ObservedAt)
            .LastOrDefault()?.Relation;

    /// <summary>When the newest supporting evidence was observed; null when there is none.</summary>
    public static DateTimeOffset? LatestSupportAt(IEnumerable<MemoryEvidence> evidence) =>
        Supporting(evidence).Select(e => (DateTimeOffset?)e.ObservedAt).Max();

    /// <summary>Knowledge reaches LongTerm once two independent sources back it; it never drops back.</summary>
    public static MemoryTier TierFor(MemoryTier current, bool pinned, int sourceCount) =>
        current == MemoryTier.LongTerm || pinned || sourceCount >= 2 ? MemoryTier.LongTerm : MemoryTier.Working;

    /// <summary>Re-derives SourceCount, Tier and Confidence of <paramref name="memory"/> from its full evidence.</summary>
    public static void Recompute(Context.Domain.Entities.Memory memory, IReadOnlyCollection<MemoryEvidence> evidence)
    {
        memory.SourceCount = CountIndependentSources(evidence);
        memory.Tier = TierFor(memory.Tier, memory.Pinned, memory.SourceCount);
        memory.Confidence = Compute(evidence, memory.Status, memory.Type);
    }

    private static IEnumerable<MemoryEvidence> Supporting(IEnumerable<MemoryEvidence> evidence) =>
        evidence.Where(e => e.Source != EvidenceSource.Code
            && e.Relation is EvidenceRelation.Created or EvidenceRelation.Reinforced or EvidenceRelation.Merged);
}
