using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Extraction;
using Context.Providers.Abstractions;
using Context.Shared;
using FluentAssertions;
using Microsoft.Extensions.AI;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Tests;

/// <summary>
/// Drives <see cref="MemoryConsolidator"/> against an in-memory store. Embeddings are hand-placed unit
/// vectors chosen by a phrase in the memory's title, so each test sits on a known side of the
/// similarity thresholds.
/// </summary>
public sealed class MemoryConsolidatorTests
{
    private const string ModelKey = "fake:embed";
    private const int Dimensions = 8;

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _sessionA = Guid.NewGuid();
    private readonly Guid _sessionB = Guid.NewGuid();
    private readonly InMemoryStore _store = new();
    private readonly FakeCodeVerifier _codeVerifier = new();
    private readonly FakeChatClient _chat = new();
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly FakeUsageRecorder _usage = new();
    private readonly MemoryConsolidator _sut;

    public MemoryConsolidatorTests()
    {
        _sut = new MemoryConsolidator(_store, _codeVerifier, new FakeProviderResolver(_chat, _embeddings), _usage);
    }

    [Fact]
    public async Task Duplicate_reinforces_one_canonical_memory_and_counts_only_independent_sessions()
    {
        _embeddings.Map("webhook retries", Axis(0));
        _chat.UseModel(judge: _ => "1|UNRELATED|would split the memory if the judge were asked");
        var candidate = WebhookRetries();

        var created = await ConsolidateAsync(candidate, _sessionA);
        var sameSession = await ConsolidateAsync(candidate, _sessionA);

        created.Outcome.Should().Be(ConsolidationOutcome.Created);
        sameSession.Outcome.Should().Be(ConsolidationOutcome.Reinforced);
        var afterRepeat = _store.Get(created.Memory.Id);
        afterRepeat.ReinforcementCount.Should().Be(1);
        afterRepeat.SourceCount.Should().Be(1, "a repeat within one session is not independent evidence");
        afterRepeat.Tier.Should().Be(MemoryTier.Working);

        var otherSession = await ConsolidateAsync(candidate with { Facts = [.. candidate.Facts, "Retries stop at the first 4xx response"] }, _sessionB);

        otherSession.Outcome.Should().Be(ConsolidationOutcome.Reinforced);
        var memory = _store.Live.Should().ContainSingle().Subject;
        memory.Id.Should().Be(created.Memory.Id);
        memory.ReinforcementCount.Should().Be(2);
        memory.SourceCount.Should().Be(2);
        memory.Tier.Should().Be(MemoryTier.LongTerm);
        memory.Confidence.Should().BeApproximately(0.75, 1e-9);
        Facts(memory).Should().HaveCount(3);
        _store.EvidenceFor(memory.Id).Select(e => e.Relation)
            .Should().Equal(EvidenceRelation.Created, EvidenceRelation.Reinforced, EvidenceRelation.Reinforced);
        _chat.Calls.Should().BeEmpty("a near-identical embedding of the same type takes the fast path");
    }

    [Fact]
    public async Task Duplicate_verdict_from_the_judge_reinforces_and_unions_facts()
    {
        _embeddings.Map("back off exponentially", Near(0.90)).Map("webhook retries", Axis(0));
        _chat.UseModel(judge: _ => "1|DUPLICATE|same retry policy");
        var original = await ConsolidateAsync(WebhookRetries(), _sessionA);

        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Configuration, "Payments webhook deliveries back off exponentially",
                "PaymentsWebhookClient waits 2, 4, 8, 16 and 32 seconds between delivery attempts.", "payments webhook",
                ["Delivery attempts wait 2, 4, 8, 16 and 32 seconds"]),
            _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Reinforced);
        result.Memory.Id.Should().Be(original.Memory.Id);
        var memory = _store.Live.Should().ContainSingle().Subject;
        Facts(memory).Should().Contain("Delivery attempts wait 2, 4, 8, 16 and 32 seconds").And.HaveCount(3);
        _chat.Calls.Should().Equal(FakeChatClient.JudgeCall);
        _usage.Purposes.Should().Equal("MemoryRelationJudge");
    }

    [Fact]
    public async Task Complementary_candidate_is_merged_into_one_memory_with_both_sets_of_facts()
    {
        const string mergedBody = "WebhookVerifier checks the Stripe-Signature header with HMAC-SHA256 using PAYMENTS_WEBHOOK_SECRET and rejects signatures older than five minutes.";
        _embeddings.Map("timestamp tolerance", Near(0.85)).Map("signature verification", Axis(0));
        _chat.UseModel(judge: _ => "1|COMPLEMENTS|adds the timestamp tolerance", merge: _ => mergedBody);
        var original = await ConsolidateAsync(SignatureVerification(), _sessionA);

        var result = await ConsolidateAsync(SignatureTolerance(), _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Merged);
        result.Memory.Id.Should().Be(original.Memory.Id);
        var memory = _store.Live.Should().ContainSingle().Subject;
        memory.Body.Should().Be(mergedBody);
        Facts(memory).Should().BeEquivalentTo(
            "Signatures are HMAC-SHA256 over the raw request body",
            "The secret comes from PAYMENTS_WEBHOOK_SECRET",
            "Signatures older than five minutes are rejected");
        memory.ReinforcementCount.Should().Be(1);
        memory.SourceCount.Should().Be(2);
        _store.EvidenceFor(memory.Id).Select(e => e.Relation).Should().Contain(EvidenceRelation.Merged);
        _store.VectorOf(memory.Id)!.Vector.Should().Equal(Axis(0), "the merged text is re-embedded");
        _chat.Calls.Should().Equal(FakeChatClient.JudgeCall, FakeChatClient.MergeCall);
    }

    [Fact]
    public async Task Offline_complementary_merge_concatenates_the_bodies()
    {
        _embeddings.Map("timestamp tolerance", Near(0.85)).Map("signature verification", Axis(0));
        var original = await ConsolidateAsync(SignatureVerification(), _sessionA);
        var complement = SignatureTolerance();

        var result = await ConsolidateAsync(complement, _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Merged);
        var memory = _store.Live.Should().ContainSingle().Subject;
        memory.Body.Should().Be($"{original.Memory.Body}\n\n{complement.Body}");
        Facts(memory).Should().HaveCount(3);
        _chat.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Complement_over_the_fact_cap_becomes_a_sibling_under_the_same_topic()
    {
        _embeddings.Map("timestamp tolerance", Near(0.85)).Map("signature verification", Axis(0));
        _chat.UseModel(judge: _ => "1|COMPLEMENTS|adds the timestamp tolerance");
        var full = SignatureVerification() with
        {
            Facts = [.. Enumerable.Range(1, MemoryConsolidator.MaxMergedFacts).Select(i => $"Verification rule number {i} holds")],
        };
        var target = Seed(full, _sessionA, Axis(0), TimeSpan.FromDays(1));

        var result = await ConsolidateAsync(SignatureTolerance(), _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Created);
        _store.Live.Should().HaveCount(2);
        result.Memory.TopicKey.Should().Be(target.TopicKey);
        result.Memory.Topic.Should().Be(target.Topic);
        Facts(_store.Get(target.Id)).Should().HaveCount(MemoryConsolidator.MaxMergedFacts);
    }

    [Fact]
    public async Task Complement_that_would_overflow_the_body_becomes_a_sibling_without_a_merge_call()
    {
        _embeddings.Map("timestamp tolerance", Near(0.85)).Map("signature verification", Axis(0));
        _chat.UseModel(judge: _ => "1|COMPLEMENTS|adds the timestamp tolerance", merge: _ => "merged");
        var filler = string.Concat(Enumerable.Repeat(" WebhookVerifier logs each rejected header.", 100));
        var longBody = (SignatureVerification().Body + filler)[..(MemoryConsolidator.MaxMergedBodyLength - SignatureTolerance().Body.Length)];
        var target = Seed(SignatureVerification() with { Body = longBody }, _sessionA, Axis(0), TimeSpan.FromDays(1));

        var result = await ConsolidateAsync(SignatureTolerance(), _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Created);
        result.Memory.TopicKey.Should().Be(target.TopicKey);
        _store.Get(target.Id).Body.Should().Be(longBody);
        _chat.Calls.Should().Equal(FakeChatClient.JudgeCall);
    }

    [Fact]
    public async Task Contradiction_marks_both_memories_contested_with_links_both_ways()
    {
        _embeddings.Map("is 14 days", Near(0.93)).Map("refund window", Axis(0));
        _chat.UseModel(judge: _ => "1|CONTRADICTS|30 days versus 14 days");
        var original = await ConsolidateAsync(RefundWindow(30), _sessionA);

        var result = await ConsolidateAsync(RefundWindow(14), _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Contested);
        var contender = _store.Get(result.Memory.Id);
        var existing = _store.Get(original.Memory.Id);
        contender.Status.Should().Be(MemoryStatus.Contested);
        existing.Status.Should().Be(MemoryStatus.Contested);
        contender.Confidence.Should().BeApproximately(0.70 - ConfidenceModel.ContestedPenalty, 1e-9);
        existing.Confidence.Should().BeApproximately(0.70 - ConfidenceModel.ContestedPenalty, 1e-9);
        _store.EvidenceFor(contender.Id).Should().ContainSingle(e => e.Relation == EvidenceRelation.Contradicted && e.SourceMemoryId == existing.Id);
        _store.EvidenceFor(existing.Id).Should().ContainSingle(e => e.Relation == EvidenceRelation.Contradicted && e.SourceMemoryId == contender.Id);
        _store.All.Should().NotContain(m => m.Status == MemoryStatus.Superseded);
    }

    [Fact]
    public async Task Offline_negation_asymmetry_contests_even_a_near_identical_embedding()
    {
        _embeddings.Map("not supported", Near(0.98)).Map("partial refunds", Axis(0));
        var original = await ConsolidateAsync(
            Knowledge(MemoryType.Behavior, "Payments partial refunds", "RefundService supports partial refunds.", "Payments refunds",
                ["Partial refunds are supported"]),
            _sessionA);

        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Behavior, "Payments partial refunds are not supported", "RefundService only refunds the full amount.",
                "Payments refunds", ["Partial refunds are not supported"]),
            _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Contested);
        _store.Get(original.Memory.Id).Status.Should().Be(MemoryStatus.Contested);
        _store.Get(result.Memory.Id).Status.Should().Be(MemoryStatus.Contested);
    }

    [Fact]
    public async Task Superseding_candidate_replaces_the_old_memory()
    {
        _embeddings.Map("raised to 60 seconds", Near(0.90)).Map("gateway timeout", Axis(0));
        _chat.UseModel(judge: _ => "1|SUPERSEDES|the timeout was raised");
        var old = Seed(GatewayTimeout(30, EvidenceSource.EventBatch), _sessionA, Axis(0), TimeSpan.FromDays(2));

        var result = await ConsolidateAsync(GatewayTimeout(60, EvidenceSource.CompactSummary), _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Superseded);
        var replacement = _store.Get(result.Memory.Id);
        var superseded = _store.Get(old.Id);
        replacement.Status.Should().Be(MemoryStatus.Active);
        superseded.Status.Should().Be(MemoryStatus.Superseded);
        superseded.SupersededById.Should().Be(replacement.Id);
        superseded.SupersededAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        _store.EvidenceFor(replacement.Id).Should().Contain(e => e.Relation == EvidenceRelation.Superseded && e.SourceMemoryId == old.Id);
        _store.EvidenceFor(old.Id).Should().Contain(e => e.Relation == EvidenceRelation.Superseded && e.SourceMemoryId == replacement.Id);
        _store.Live.Should().ContainSingle().Which.Id.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task Newer_but_equally_reliable_claim_is_contested_instead_of_superseding()
    {
        _embeddings.Map("raised to 60 seconds", Near(0.90)).Map("gateway timeout", Axis(0));
        _chat.UseModel(judge: _ => "1|SUPERSEDES|the timeout was raised");
        var old = Seed(GatewayTimeout(30, EvidenceSource.EventBatch), _sessionA, Axis(0), TimeSpan.FromDays(2));

        var result = await ConsolidateAsync(GatewayTimeout(60, EvidenceSource.EventBatch), _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Contested);
        var kept = _store.Get(old.Id);
        kept.Status.Should().Be(MemoryStatus.Contested);
        kept.SupersededById.Should().BeNull();
        _store.Get(result.Memory.Id).Status.Should().Be(MemoryStatus.Contested);
        _store.All.Should().NotContain(m => m.Status == MemoryStatus.Superseded);
    }

    [Fact]
    public async Task Claim_observed_before_the_targets_latest_support_is_contested_even_when_more_reliable()
    {
        _embeddings.Map("raised to 60 seconds", Near(0.90)).Map("gateway timeout", Axis(0));
        _chat.UseModel(judge: _ => "1|SUPERSEDES|the timeout was raised");
        var old = Seed(GatewayTimeout(30, EvidenceSource.EventBatch), _sessionA, Axis(0), TimeSpan.FromDays(2));
        var observedAt = DateTimeOffset.UtcNow.AddDays(-3);

        var result = await ConsolidateAsync(GatewayTimeout(60, EvidenceSource.CompactSummary) with { ObservedAt = observedAt }, _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Contested);
        _store.Get(old.Id).Status.Should().Be(MemoryStatus.Contested);
        _store.EvidenceFor(result.Memory.Id).Should().OnlyContain(e => e.ObservedAt == observedAt);
    }

    [Theory]
    [InlineData(false, ConsolidationOutcome.Superseded)]
    [InlineData(true, ConsolidationOutcome.Contested)]
    public async Task Equally_reliable_claim_the_code_verifies_supersedes_only_a_target_the_code_does_not_verify(
        bool targetVerified, ConsolidationOutcome expected)
    {
        _embeddings.Map("raised to 60 seconds", Near(0.90)).Map("gateway timeout", Axis(0));
        _chat.UseModel(judge: _ => "1|SUPERSEDES|the timeout was raised");
        _codeVerifier.Check = (paths, _) => paths.Any(p => p.EndsWith("GatewaySettings.cs", StringComparison.Ordinal))
            ? new CodeCheck(CodeCheckResult.Verified, "found")
            : CodeCheck.Unknown;
        var target = GatewayTimeout(30, EvidenceSource.EventBatch);
        var old = Seed(
            targetVerified ? target with { FilesRead = ["src/Payments/Gateway/GatewaySettings.cs"] } : target,
            _sessionA, Axis(0), TimeSpan.FromDays(2));

        var result = await ConsolidateAsync(
            GatewayTimeout(60, EvidenceSource.EventBatch) with { FilesRead = ["src/Payments/Gateway/GatewaySettings.cs"] }, _sessionB);

        result.Outcome.Should().Be(expected);
        _store.Get(old.Id).Status.Should().Be(targetVerified ? MemoryStatus.Contested : MemoryStatus.Superseded);
    }

    [Fact]
    public async Task Newer_but_less_reliable_claim_is_contested_instead_of_superseding()
    {
        _embeddings.Map("single-entry", Near(0.90)).Map("double-entry", Axis(0));
        _chat.UseModel(judge: _ => "1|SUPERSEDES|the ledger changed");
        var rule = Seed(
            Knowledge(MemoryType.Decision, "Payments ledger uses double-entry bookkeeping",
                "LedgerService records every movement as a debit and a matching credit.", "Payments ledger",
                ["Every ledger movement is a debit and a matching credit"], EvidenceSource.Explicit),
            null, Axis(0), TimeSpan.FromDays(10));

        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Decision, "Payments ledger uses single-entry bookkeeping",
                "LedgerService records each movement once.", "Payments ledger", ["Every ledger movement is recorded once"]),
            _sessionA);

        result.Outcome.Should().Be(ConsolidationOutcome.Contested);
        var kept = _store.Get(rule.Id);
        kept.Status.Should().Be(MemoryStatus.Contested);
        kept.SupersededById.Should().BeNull();
        kept.Confidence.Should().BeApproximately(0.90 - ConfidenceModel.ContestedPenalty, 1e-9, "the explicit floor does not hold while contested");
        _store.Get(result.Memory.Id).Status.Should().Be(MemoryStatus.Contested);
    }

    [Fact]
    public async Task Newer_claim_refuted_by_code_loses_to_the_verified_memory()
    {
        _embeddings.Map("LegacyLedger", Near(0.90)).Map("LedgerService", Axis(0));
        _chat.UseModel(judge: _ => "1|SUPERSEDES|the ledger moved");
        _codeVerifier.Check = (paths, _) =>
            paths.Any(p => p.EndsWith("LegacyLedger.cs", StringComparison.Ordinal)) ? new CodeCheck(CodeCheckResult.Refuted, "missing")
            : paths.Any(p => p.EndsWith("LedgerService.cs", StringComparison.Ordinal)) ? new CodeCheck(CodeCheckResult.Verified, "found")
            : CodeCheck.Unknown;
        var verified = Seed(
            Knowledge(MemoryType.Architecture, "Payments ledger lives in LedgerService", "Ledger writes go through LedgerService.",
                "Payments ledger", filesRead: ["src/Payments/Ledger/LedgerService.cs"]),
            _sessionA, Axis(0), TimeSpan.FromDays(3));

        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Architecture, "Payments ledger lives in LegacyLedger", "Ledger writes go through LegacyLedger.",
                "Payments ledger", source: EvidenceSource.CompactSummary, filesRead: ["src/Payments/Ledger/LegacyLedger.cs"]),
            _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Superseded);
        result.Memory.Id.Should().Be(verified.Id);
        var loser = _store.All.Single(m => m.Id != verified.Id);
        loser.Status.Should().Be(MemoryStatus.Superseded);
        loser.SupersededById.Should().Be(verified.Id);
        var winner = _store.Get(verified.Id);
        winner.Status.Should().Be(MemoryStatus.Active);
        winner.Confidence.Should().BeApproximately(0.70 + ConfidenceModel.VerifiedBonus, 1e-9);
        _store.EvidenceFor(verified.Id).Should().ContainSingle(e => e.Relation == EvidenceRelation.Verified && e.Source == EvidenceSource.Code && e.SessionId == null);
        _store.EvidenceFor(loser.Id).Should().ContainSingle(e => e.Relation == EvidenceRelation.Refuted);
    }

    [Fact]
    public async Task Explicit_remember_resolves_a_contest_in_favour_of_the_remembered_claim()
    {
        _embeddings.Map("is 14 days", Near(0.93)).Map("refund window", Axis(0));
        _chat.UseModel(judge: _ => "1|CONTRADICTS|30 days versus 14 days");
        var loser = await ConsolidateAsync(RefundWindow(30), _sessionA);
        var winner = await ConsolidateAsync(RefundWindow(14), _sessionB);

        var result = await ConsolidateAsync(RefundWindow(14) with { Source = EvidenceSource.Explicit }, null);

        result.Outcome.Should().Be(ConsolidationOutcome.Reinforced);
        result.Memory.Id.Should().Be(winner.Memory.Id);
        var resolved = _store.Get(winner.Memory.Id);
        resolved.Status.Should().Be(MemoryStatus.Active);
        resolved.Confidence.Should().BeApproximately(0.95, 1e-9);
        var dropped = _store.Get(loser.Memory.Id);
        dropped.Status.Should().Be(MemoryStatus.Superseded);
        dropped.SupersededById.Should().Be(winner.Memory.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_memories_from_the_same_repository_stay_separate(bool useModel)
    {
        _embeddings
            .Map("Artifactory", Shared(1))
            .Map("multi-pack-index", Shared(2))
            .Map("AWS_BILLS_GQL_URL", Shared(3))
            .Map("GSI2", Shared(4));
        if (useModel)
        {
            _chat.UseModel(judge: _ => string.Join('\n', Enumerable.Range(1, MemoryConsolidator.MaxNeighbours).Select(i => $"{i}|UNRELATED|different subject")));
        }

        MemoryCandidate[] candidates =
        [
            Knowledge(MemoryType.Issue, "npm install fails with 401 from Artifactory",
                "npm install in payments-api returns 401 Unauthorized from Artifactory when the token in .npmrc has expired; regenerate it with jf npm-config.",
                "npm Artifactory auth", filesRead: [".npmrc", "README.md"]),
            Knowledge(MemoryType.Issue, "Git fetch fails on a corrupt multi-pack-index",
                "git fetch in payments-api aborts with 'multi-pack-index file is too small'; delete .git/objects/pack/multi-pack-index and run git multi-pack-index write.",
                "Git multi-pack-index", filesRead: ["README.md"]),
            Knowledge(MemoryType.Configuration, "AWS_BILLS_GQL_URL points the bills client at the GraphQL gateway",
                "BillsGraphQlClient reads AWS_BILLS_GQL_URL; local runs use the staging gateway URL from appsettings.Development.json.",
                "Bills GraphQL client", filesRead: ["README.md", "src/Bills/appsettings.Development.json"]),
            Knowledge(MemoryType.Architecture, "Payments table GSI2 keys",
                "The Payments DynamoDB table's GSI2 uses GSI2PK = MERCHANT#<id> and GSI2SK = CREATED#<timestamp> to list payments by merchant.",
                "Payments DynamoDB table", filesRead: ["README.md"]),
        ];

        var outcomes = new List<ConsolidationOutcome>();
        foreach (var candidate in candidates)
        {
            outcomes.Add((await ConsolidateAsync(candidate, _sessionA)).Outcome);
        }

        outcomes.Should().AllBeEquivalentTo(ConsolidationOutcome.Created);
        _store.Live.Should().HaveCount(4);
        _store.Live.Should().OnlyContain(m => m.ReinforcementCount == 0 && m.Status == MemoryStatus.Active);
        _store.Live.Select(m => m.Topic).Should().OnlyHaveUniqueItems();
        if (useModel)
        {
            _chat.Calls.Should().HaveCount(3, "every later memory had neighbours and was judged");
        }
    }

    [Fact]
    public async Task Event_is_stored_as_an_event_and_never_merged_into_knowledge()
    {
        _embeddings.Map("webhook retries", Axis(0));
        _chat.UseModel(judge: _ => "1|DUPLICATE|would merge if asked");
        var knowledge = await ConsolidateAsync(WebhookRetries(), _sessionA);
        var deployed = new MemoryCandidate(
            MemoryType.Event, "Deployed the webhook retries change to staging",
            "PaymentsWebhookClient retry changes for PAY-231 went to staging.", EvidenceSource.EventBatch,
            [], [], [], [], "Payments webhooks");

        var first = await ConsolidateAsync(deployed, _sessionA);
        var repeat = await ConsolidateAsync(deployed with { Title = "deployed the webhook retries change to staging." }, _sessionA);
        var otherSession = await ConsolidateAsync(deployed, _sessionB);

        first.Outcome.Should().Be(ConsolidationOutcome.EventRecorded);
        first.Memory.Type.Should().Be(MemoryType.Event);
        repeat.Outcome.Should().Be(ConsolidationOutcome.EventRecorded);
        repeat.Memory.Id.Should().Be(first.Memory.Id);
        _store.Get(first.Memory.Id).ReinforcementCount.Should().Be(1);
        otherSession.Memory.Id.Should().NotBe(first.Memory.Id, "events are deduped only within their session");

        var untouched = _store.Get(knowledge.Memory.Id);
        untouched.ReinforcementCount.Should().Be(0);
        untouched.Body.Should().Be(knowledge.Memory.Body);
        _store.VectorOf(first.Memory.Id).Should().BeNull("events are not embedded");
        _chat.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Change_log_titled_knowledge_is_recorded_as_an_event()
    {
        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Discovery, "Added retries to PaymentsWebhookClient", "Retries were added for PAY-231.", "Payments webhooks"),
            _sessionA);

        result.Outcome.Should().Be(ConsolidationOutcome.EventRecorded);
        result.Memory.Type.Should().Be(MemoryType.Event);
    }

    [Fact]
    public async Task Judge_failure_falls_back_to_the_offline_heuristic()
    {
        _embeddings.Map("back off exponentially", Near(0.95)).Map("webhook retries", Axis(0));
        _chat.UseModel(judge: _ => throw new HttpRequestException("provider down"));
        var original = await ConsolidateAsync(WebhookRetries(), _sessionA);

        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Configuration, "Payments webhook deliveries back off exponentially",
                "PaymentsWebhookClient doubles the wait between delivery attempts.", "Payments webhooks"),
            _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Reinforced, "cosine 0.95 with a shared symbol is an offline duplicate");
        result.Memory.Id.Should().Be(original.Memory.Id);
        _chat.Calls.Should().Equal(FakeChatClient.JudgeCall);
    }

    [Fact]
    public async Task Verified_candidate_carries_code_evidence_and_the_verified_bonus()
    {
        _embeddings.Map("signature verification", Axis(0));
        _codeVerifier.Check = (_, _) => new CodeCheck(CodeCheckResult.Verified, "found in code: WebhookVerifier.cs");

        var result = await ConsolidateAsync(SignatureVerification() with { FilesModified = ["src/Payments/Webhooks/WebhookVerifier.cs"] }, _sessionA);

        result.Outcome.Should().Be(ConsolidationOutcome.Created);
        result.Memory.Confidence.Should().BeApproximately(0.70 + ConfidenceModel.VerifiedBonus, 1e-9);
        _store.EvidenceFor(result.Memory.Id).Should().ContainSingle(e =>
            e.Relation == EvidenceRelation.Verified && e.Source == EvidenceSource.Code && e.SessionId == null && e.Note == "found in code: WebhookVerifier.cs");
        _codeVerifier.Paths.Should().ContainSingle().Which.Should().Contain("src/Payments/Webhooks/WebhookVerifier.cs");
    }

    [Fact]
    public async Task Candidate_sharing_two_strong_entities_adopts_the_neighbours_topic()
    {
        _embeddings.Map("signing secret", Near(0.70)).Map("signature verification", Axis(0));
        var existing = await ConsolidateAsync(SignatureVerification(), _sessionA);

        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Procedure, "Rotate the Stripe signing secret",
                "Update PAYMENTS_WEBHOOK_SECRET, then restart the pods so WebhookVerifier reloads it.", "Stripe secrets"),
            _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Created);
        result.Memory.Topic.Should().Be(existing.Memory.Topic);
        result.Memory.TopicKey.Should().Be(existing.Memory.TopicKey);
    }

    [Fact]
    public async Task Candidate_with_an_equal_topic_key_takes_the_existing_display_topic()
    {
        _embeddings.Map("Replay", Near(0.70)).Map("signature verification", Axis(0));
        await ConsolidateAsync(SignatureVerification(), _sessionA);

        var result = await ConsolidateAsync(
            Knowledge(MemoryType.Procedure, "Replay a Stripe event", "Use the Stripe dashboard to resend an event.", "payment webhook"),
            _sessionB);

        result.Memory.Topic.Should().Be("Payments webhooks");
        result.Memory.TopicKey.Should().Be("payment-webhook");
    }

    [Fact]
    public async Task Neighbour_embedded_by_another_model_is_re_embedded_before_comparison()
    {
        _embeddings.Map("webhook retries", Axis(0));
        var old = Seed(WebhookRetries(), _sessionA, [1f, 0f, 0f], TimeSpan.FromDays(1), modelKey: "old:model");

        var result = await ConsolidateAsync(WebhookRetries(), _sessionB);

        result.Outcome.Should().Be(ConsolidationOutcome.Reinforced);
        result.Memory.Id.Should().Be(old.Id);
        _store.VectorOf(old.Id)!.ModelKey.Should().Be(ModelKey);
    }

    [Fact]
    public async Task Rejected_candidate_throws()
    {
        var act = () => ConsolidateAsync(Knowledge(MemoryType.Fact, "Title only", "   ", "Payments"), _sessionA);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*empty body*");
    }

    private Task<ConsolidationResult> ConsolidateAsync(MemoryCandidate candidate, Guid? sessionId) =>
        _sut.ConsolidateAsync(_projectId, sessionId, candidate, isInteractive: false, CancellationToken.None);

    private DomainMemory Seed(MemoryCandidate candidate, Guid? sessionId, float[] vector, TimeSpan age, string modelKey = ModelKey)
    {
        var at = DateTimeOffset.UtcNow - age;
        var memory = new DomainMemory
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Type = candidate.Type,
            Title = candidate.Title,
            Body = candidate.Body,
            Topic = candidate.Topic,
            TopicKey = TopicNormalizer.Normalize(candidate.Topic),
            FactsJson = JsonSerializer.Serialize(candidate.Facts),
            FilesReadJson = candidate.FilesRead.Count > 0 ? JsonSerializer.Serialize(candidate.FilesRead) : null,
            FilesModifiedJson = candidate.FilesModified.Count > 0 ? JsonSerializer.Serialize(candidate.FilesModified) : null,
            CreatedFromSessionId = sessionId,
            CreatedAt = at,
            LastReinforcedAt = at,
        };
        var evidence = new MemoryEvidence
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            MemoryId = memory.Id,
            Relation = EvidenceRelation.Created,
            Source = candidate.Source,
            SessionId = sessionId,
            ObservedAt = at,
        };
        memory.SourceCount = ConfidenceModel.CountIndependentSources([evidence]);
        memory.Confidence = ConfidenceModel.Compute([evidence], memory.Status, memory.Type);
        var entities = EntityExtractor.Extract(candidate)
            .Select(e => new MemoryEntity { ProjectId = _projectId, MemoryId = memory.Id, Key = e.Key, Display = e.Display });

        _store.Add(memory, new MemoryVector(modelKey, vector), entities, [evidence]);
        return memory;
    }

    private static MemoryCandidate Knowledge(
        MemoryType type,
        string title,
        string body,
        string topic,
        IReadOnlyList<string>? facts = null,
        EvidenceSource source = EvidenceSource.EventBatch,
        IReadOnlyList<string>? filesRead = null) =>
        new(type, title, body, source, facts ?? [], [], filesRead ?? [], [], topic);

    private static MemoryCandidate WebhookRetries() => Knowledge(
        MemoryType.Configuration,
        "Payments webhook retries use exponential backoff",
        "PaymentsWebhookClient retries failed deliveries five times with exponential backoff starting at 2 seconds.",
        "Payments webhooks",
        ["PaymentsWebhookClient retries failed deliveries five times", "Backoff starts at 2 seconds and doubles"]);

    private static MemoryCandidate SignatureVerification() => Knowledge(
        MemoryType.Configuration,
        "Payments webhook signature verification",
        "WebhookVerifier checks the Stripe-Signature header with HMAC-SHA256 using PAYMENTS_WEBHOOK_SECRET.",
        "Payments webhooks",
        ["Signatures are HMAC-SHA256 over the raw request body", "The secret comes from PAYMENTS_WEBHOOK_SECRET"]);

    private static MemoryCandidate SignatureTolerance() => Knowledge(
        MemoryType.Configuration,
        "Payments webhook signature timestamp tolerance",
        "WebhookVerifier rejects a Stripe-Signature whose timestamp is more than five minutes old.",
        "payments webhook",
        ["Signatures older than five minutes are rejected"]);

    private static MemoryCandidate RefundWindow(int days) => Knowledge(
        MemoryType.Configuration,
        days == 30 ? "Payments refund window" : $"Payments refund window is {days} days",
        $"RefundPolicy allows refunds within {days} days of capture.",
        "Payments refunds",
        [$"Refunds are allowed within {days} days of capture"]);

    private static MemoryCandidate GatewayTimeout(int seconds, EvidenceSource source) => Knowledge(
        MemoryType.Configuration,
        seconds == 30 ? "Payments gateway timeout" : $"Payments gateway timeout raised to {seconds} seconds",
        $"PaymentsGatewayClient gives up on the card gateway after {seconds} seconds.",
        "Payments gateway",
        [$"The card gateway timeout is {seconds} seconds"],
        source);

    private static IReadOnlyList<string> Facts(DomainMemory memory) =>
        JsonSerializer.Deserialize<List<string>>(memory.FactsJson ?? "[]") ?? [];

    private static float[] Axis(int index)
    {
        var vector = new float[Dimensions];
        vector[index] = 1f;
        return vector;
    }

    /// <summary>A unit vector at exactly <paramref name="cosine"/> to <c>Axis(0)</c>.</summary>
    private static float[] Near(double cosine)
    {
        var vector = new float[Dimensions];
        vector[0] = (float)cosine;
        vector[Dimensions - 1] = (float)Math.Sqrt(1 - cosine * cosine);
        return vector;
    }

    /// <summary>Unit vectors sharing one component: any two of them are at cosine 0.75, above the
    /// neighbour floor but below every relation threshold.</summary>
    private static float[] Shared(int index)
    {
        var vector = new float[Dimensions];
        vector[0] = (float)Math.Sqrt(0.75);
        vector[index] = 0.5f;
        return vector;
    }

    /// <summary>Offline ("noop") until <see cref="UseModel"/> scripts the relation judge and merge answers.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        public const string JudgeCall = "judge";
        public const string MergeCall = "merge";

        private string _providerName = "noop";
        private Func<string, string> _judge = _ => "";
        private Func<string, string> _merge = _ => "";

        public List<string> Calls { get; } = [];

        public void UseModel(Func<string, string>? judge = null, Func<string, string>? merge = null)
        {
            _providerName = "fake";
            _judge = judge ?? _judge;
            _merge = merge ?? _merge;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var list = messages.ToList();
            var system = list.First(m => m.Role == ChatRole.System).Text;
            var user = list.First(m => m.Role == ChatRole.User).Text;
            var (call, answer) = system switch
            {
                MemoryRelationJudge.SystemPrompt => (JudgeCall, _judge),
                MemoryMergePrompt.SystemPrompt => (MergeCall, _merge),
                _ => throw new InvalidOperationException("Unexpected prompt."),
            };

            Calls.Add(call);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(user))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(_providerName, null, "model") : null;

        public void Dispose()
        {
        }
    }
}
