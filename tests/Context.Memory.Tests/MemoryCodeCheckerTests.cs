using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class MemoryCodeCheckerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly CodeCheck Verified = new(CodeCheckResult.Verified, "found");

    [Fact]
    public void A_verification_after_a_refutation_is_recorded_again()
    {
        List<MemoryEvidence> history = [CodeEvidence(EvidenceRelation.Verified, T0), CodeEvidence(EvidenceRelation.Refuted, T0.AddDays(1))];

        var row = MemoryCodeChecker.Evidence(Guid.NewGuid(), Guid.NewGuid(), Verified, history, T0.AddDays(2));

        row.Should().NotBeNull();
        row!.Relation.Should().Be(EvidenceRelation.Verified);
        row.Source.Should().Be(EvidenceSource.Code);
        row.ObservedAt.Should().Be(T0.AddDays(2));
    }

    [Fact]
    public void An_outcome_equal_to_the_latest_check_or_unknown_adds_nothing()
    {
        List<MemoryEvidence> history = [CodeEvidence(EvidenceRelation.Refuted, T0), CodeEvidence(EvidenceRelation.Verified, T0.AddDays(1))];

        MemoryCodeChecker.Evidence(Guid.NewGuid(), Guid.NewGuid(), Verified, history, T0.AddDays(2)).Should().BeNull();
        MemoryCodeChecker.Evidence(Guid.NewGuid(), Guid.NewGuid(), CodeCheck.Unknown, [], T0).Should().BeNull();
    }

    private static MemoryEvidence CodeEvidence(EvidenceRelation relation, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        Relation = relation,
        Source = EvidenceSource.Code,
        ObservedAt = at,
    };
}
