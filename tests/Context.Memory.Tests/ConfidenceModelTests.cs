using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class ConfidenceModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(EvidenceSource.Explicit, 0.90)]
    [InlineData(EvidenceSource.Code, 0.90)]
    [InlineData(EvidenceSource.CompactSummary, 0.75)]
    [InlineData(EvidenceSource.EventBatch, 0.70)]
    [InlineData(EvidenceSource.Consolidation, 0.70)]
    [InlineData(EvidenceSource.Legacy, 0.65)]
    [InlineData(EvidenceSource.Heuristic, 0.40)]
    public void Reliability_ranks_each_source(EvidenceSource source, double expected)
    {
        ConfidenceModel.Reliability(source).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 0.70)]
    [InlineData(2, 0.75)]
    [InlineData(3, 0.80)]
    [InlineData(4, 0.85)]
    [InlineData(6, 0.85)]
    public void Compute_adds_a_capped_bonus_per_independent_session(int sessions, double expected)
    {
        var evidence = Enumerable.Range(0, sessions)
            .Select(i => Evidence(EvidenceSource.EventBatch, i == 0 ? EvidenceRelation.Created : EvidenceRelation.Reinforced))
            .ToList();

        ConfidenceModel.Compute(evidence, MemoryStatus.Active, MemoryType.Fact).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Repeats_within_one_session_count_as_one_source()
    {
        var session = Guid.NewGuid();
        List<MemoryEvidence> evidence =
        [
            Evidence(EvidenceSource.EventBatch, EvidenceRelation.Created, session),
            Evidence(EvidenceSource.EventBatch, EvidenceRelation.Reinforced, session),
        ];

        ConfidenceModel.CountIndependentSources(evidence).Should().Be(1);
        ConfidenceModel.Compute(evidence, MemoryStatus.Active, MemoryType.Fact).Should().BeApproximately(0.70, 1e-9);
    }

    [Fact]
    public void Sessionless_explicit_assertions_count_separately_and_source_memories_count_once_each()
    {
        var sourceMemory = Guid.NewGuid();
        List<MemoryEvidence> evidence =
        [
            Evidence(EvidenceSource.Explicit, EvidenceRelation.Created, sessionless: true),
            Evidence(EvidenceSource.Explicit, EvidenceRelation.Reinforced, sessionless: true),
            Evidence(EvidenceSource.Consolidation, EvidenceRelation.Merged, sessionless: true, sourceMemory: sourceMemory),
            Evidence(EvidenceSource.Consolidation, EvidenceRelation.Merged, sessionless: true, sourceMemory: sourceMemory),
        ];

        ConfidenceModel.CountIndependentSources(evidence).Should().Be(3);
    }

    [Fact]
    public void A_merged_link_does_not_add_to_the_copied_evidence_of_an_explicit_only_source()
    {
        var sourceMemory = Guid.NewGuid();
        List<MemoryEvidence> evidence =
        [
            Evidence(EvidenceSource.Explicit, EvidenceRelation.Created, sessionless: true, sourceMemory: sourceMemory),
            Evidence(EvidenceSource.Consolidation, EvidenceRelation.Merged, sessionless: true, sourceMemory: sourceMemory),
        ];

        ConfidenceModel.CountIndependentSources(evidence).Should().Be(1);
    }

    [Fact]
    public void Only_created_reinforced_and_merged_non_code_evidence_supports_a_memory()
    {
        List<MemoryEvidence> evidence =
        [
            Evidence(EvidenceSource.Heuristic, EvidenceRelation.Created),
            Evidence(EvidenceSource.Explicit, EvidenceRelation.Contradicted),
            Evidence(EvidenceSource.Code, EvidenceRelation.Created),
        ];

        ConfidenceModel.BestSourceReliability(evidence).Should().Be(0.40);
        ConfidenceModel.CountIndependentSources(evidence).Should().Be(1);
    }

    [Fact]
    public void A_memory_without_evidence_ranks_as_legacy()
    {
        ConfidenceModel.Compute([], MemoryStatus.Active, MemoryType.Fact).Should().BeApproximately(0.65, 1e-9);
    }

    [Fact]
    public void Only_the_latest_code_check_counts()
    {
        var created = Evidence(EvidenceSource.EventBatch, EvidenceRelation.Created);
        var refutedThenVerified = new List<MemoryEvidence>
        {
            created,
            Evidence(EvidenceSource.Code, EvidenceRelation.Refuted, at: T0.AddDays(1)),
            Evidence(EvidenceSource.Code, EvidenceRelation.Verified, at: T0.AddDays(2)),
        };
        var verifiedThenRefuted = new List<MemoryEvidence>
        {
            created,
            Evidence(EvidenceSource.Code, EvidenceRelation.Verified, at: T0.AddDays(1)),
            Evidence(EvidenceSource.Code, EvidenceRelation.Refuted, at: T0.AddDays(2)),
        };

        ConfidenceModel.Compute(refutedThenVerified, MemoryStatus.Active, MemoryType.Fact).Should().BeApproximately(0.75, 1e-9);
        ConfidenceModel.Compute(verifiedThenRefuted, MemoryStatus.Active, MemoryType.Fact).Should().BeApproximately(0.50, 1e-9);
    }

    [Fact]
    public void Latest_support_is_the_newest_supporting_observation()
    {
        List<MemoryEvidence> evidence =
        [
            Evidence(EvidenceSource.EventBatch, EvidenceRelation.Created, at: T0),
            Evidence(EvidenceSource.CompactSummary, EvidenceRelation.Reinforced, at: T0.AddDays(2)),
            Evidence(EvidenceSource.Code, EvidenceRelation.Verified, at: T0.AddDays(5)),
            Evidence(EvidenceSource.EventBatch, EvidenceRelation.Contradicted, at: T0.AddDays(6)),
        ];

        ConfidenceModel.LatestSupportAt(evidence).Should().Be(T0.AddDays(2));
        ConfidenceModel.LatestSupportAt([]).Should().BeNull();
    }

    [Fact]
    public void Contested_memories_lose_confidence()
    {
        List<MemoryEvidence> evidence = [Evidence(EvidenceSource.EventBatch, EvidenceRelation.Created)];

        ConfidenceModel.Compute(evidence, MemoryStatus.Contested, MemoryType.Fact).Should().BeApproximately(0.45, 1e-9);
    }

    [Theory]
    [InlineData(MemoryType.Decision, MemoryStatus.Active, 0.85)]
    [InlineData(MemoryType.Constraint, MemoryStatus.Active, 0.85)]
    [InlineData(MemoryType.Fact, MemoryStatus.Active, 0.70)]
    [InlineData(MemoryType.Decision, MemoryStatus.Contested, 0.45)]
    public void Explicit_decisions_and_constraints_keep_a_floor_unless_contested(MemoryType type, MemoryStatus status, double expected)
    {
        List<MemoryEvidence> evidence =
        [
            Evidence(EvidenceSource.Explicit, EvidenceRelation.Created),
            Evidence(EvidenceSource.Code, EvidenceRelation.Refuted, at: T0.AddDays(1)),
        ];

        ConfidenceModel.Compute(evidence, status, type).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Compute_clamps_to_the_configured_range()
    {
        List<MemoryEvidence> weak =
        [
            Evidence(EvidenceSource.Heuristic, EvidenceRelation.Created),
            Evidence(EvidenceSource.Code, EvidenceRelation.Refuted, at: T0.AddDays(1)),
        ];
        var strong = Enumerable.Range(0, 5)
            .Select(_ => Evidence(EvidenceSource.Explicit, EvidenceRelation.Reinforced))
            .Append(Evidence(EvidenceSource.Code, EvidenceRelation.Verified, at: T0.AddDays(1)))
            .ToList();

        ConfidenceModel.Compute(weak, MemoryStatus.Contested, MemoryType.Fact).Should().Be(ConfidenceModel.Min);
        ConfidenceModel.Compute(strong, MemoryStatus.Active, MemoryType.Fact).Should().Be(ConfidenceModel.Max);
    }

    [Theory]
    [InlineData(MemoryTier.Working, false, 1, MemoryTier.Working)]
    [InlineData(MemoryTier.Working, false, 2, MemoryTier.LongTerm)]
    [InlineData(MemoryTier.Working, true, 1, MemoryTier.LongTerm)]
    [InlineData(MemoryTier.LongTerm, false, 1, MemoryTier.LongTerm)]
    public void TierFor_promotes_on_a_second_source_or_pin_and_never_demotes(MemoryTier current, bool pinned, int sources, MemoryTier expected)
    {
        ConfidenceModel.TierFor(current, pinned, sources).Should().Be(expected);
    }

    private static MemoryEvidence Evidence(
        EvidenceSource source,
        EvidenceRelation relation,
        Guid? session = null,
        bool sessionless = false,
        Guid? sourceMemory = null,
        DateTimeOffset? at = null) => new()
    {
        Id = Guid.NewGuid(),
        Source = source,
        Relation = relation,
        SessionId = sessionless ? null : session ?? Guid.NewGuid(),
        SourceMemoryId = sourceMemory,
        ObservedAt = at ?? T0,
    };
}
