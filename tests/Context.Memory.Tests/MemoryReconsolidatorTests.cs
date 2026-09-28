using System.Text;
using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Extraction;
using Context.Shared;
using FluentAssertions;
using Microsoft.Extensions.AI;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Tests;

/// <summary>
/// Drives <see cref="MemoryReconsolidator"/> against an in-memory store with memories taken from a real
/// project database. Seeded vectors are hand-placed so a test decides which memories are similar; the
/// scripted chat model finds source numbers by title in the prompt it is sent.
/// </summary>
public sealed class MemoryReconsolidatorTests
{
    private const string ModelKey = "fake:embed";
    private const int Dimensions = 8;
    private const string NoneSections = "CONTRADICTIONS: none\nKEEP_SEPARATE: none\n";

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly InMemoryStore _store = new();
    private readonly FakeCodeVerifier _codeVerifier = new();
    private readonly ReconsolidationChat _chat = new();
    private readonly FakeEmbeddingGenerator _embeddings = new();
    private readonly FakeUsageRecorder _usage = new();
    private readonly MemoryReconsolidator _sut;
    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddDays(-1);

    public MemoryReconsolidatorTests()
    {
        _sut = new MemoryReconsolidator(_store, _codeVerifier, new FakeProviderResolver(_chat, _embeddings), _usage);
    }

    [Fact]
    public async Task Related_group_becomes_one_canonical_with_every_fact_and_its_sources_are_superseded_not_deleted()
    {
        var refresh = Seed(SessionAmountRefresh(), Shared(1));
        var rules = Seed(RefreshRules(), Shared(2));
        var reuse = Seed(SessionReuse(), Shared(3));
        var allFacts = new[] { refresh, rules, reuse }.SelectMany(Facts).ToList();
        _chat.Answer = user => PaymentsCanonical(user, allFacts, refresh, rules, reuse);
        _embeddings.Map("", Axis(7));

        var plan = await PlanAsync();

        var group = plan.Groups.Should().ContainSingle().Subject;
        group.Ambiguous.Should().BeFalse(group.Note);
        var planned = group.Canonicals.Should().ContainSingle().Subject;
        planned.SourceIds.Should().BeEquivalentTo(new[] { refresh.Id, rules.Id, reuse.Id });
        planned.FilesRead.Should().Contain("src/dotnet/domain_api/Payments.DomainAppApi.Svc/CheckoutSessionV1Manager.cs");
        plan.Stats.Should().Match<ReconsolidationStats>(s => s.Canonicals == 1 && s.SourcesMerged == 3 && s.UsedModel);
        plan.Untouched.Should().Be(0);

        var applied = await ApplyAsync(plan);

        applied.Outcome!.AppliedGroups.Should().Equal(group.Index);
        var canonical = _store.Live.Should().ContainSingle().Subject;
        Facts(canonical).Should().BeEquivalentTo(allFacts);
        canonical.Status.Should().Be(MemoryStatus.Active);
        canonical.SourceCount.Should().Be(3, "each source came from its own session");
        canonical.Tier.Should().Be(MemoryTier.LongTerm);
        _store.VectorOf(canonical.Id)!.ModelKey.Should().Be(ModelKey);
        _store.EvidenceFor(canonical.Id).Where(e => e.Relation == EvidenceRelation.Merged).Select(e => e.SourceMemoryId)
            .Should().BeEquivalentTo(new Guid?[] { refresh.Id, rules.Id, reuse.Id });

        _store.All.Should().HaveCount(4, "sources are superseded, never deleted");
        foreach (var source in new[] { refresh, rules, reuse })
        {
            var stored = _store.Get(source.Id);
            stored.Status.Should().Be(MemoryStatus.Superseded);
            stored.SupersededById.Should().Be(canonical.Id);
            _store.EvidenceFor(source.Id).Should().Contain(e => e.Relation == EvidenceRelation.Superseded && e.SourceMemoryId == canonical.Id);
        }

        _usage.Purposes.Should().Equal("MemoryReconsolidation");
    }

    [Fact]
    public async Task Unrelated_subjects_stay_separate_even_with_identical_embeddings_and_only_the_npm_pair_merges()
    {
        var npmSandbox = Seed(NpmSandboxInstall(), Axis(0));
        var npmE2e = Seed(NpmE2eInstall(), Axis(0));
        var git = Seed(GitMultiPackIndex(), Axis(0));
        var aws = Seed(BillsGqlUrl(), Axis(0));
        var gsi = Seed(PatientBillGsi(), Axis(0));
        _chat.Answer = user => NpmCanonical(user, npmSandbox, npmE2e);
        _embeddings.Map("", Axis(7));

        var plan = await PlanAsync();

        var group = plan.Groups.Should().ContainSingle().Subject;
        group.Sources.Select(s => s.Id).Should().BeEquivalentTo(new[] { npmSandbox.Id, npmE2e.Id });
        var prompt = _chat.Calls.Should().ContainSingle().Subject;
        SourceTitles(prompt).Should().BeEquivalentTo(npmSandbox.Title, npmE2e.Title);
        plan.Untouched.Should().Be(3);
        plan.ReclassifiedEvents.Should().BeEmpty("a change-log title that carries facts stays knowledge");

        await ApplyAsync(plan);

        _store.Get(npmSandbox.Id).Status.Should().Be(MemoryStatus.Superseded);
        _store.Get(npmE2e.Id).Status.Should().Be(MemoryStatus.Superseded);
        foreach (var separate in new[] { git, aws, gsi })
        {
            var stored = _store.Get(separate.Id);
            stored.Status.Should().Be(MemoryStatus.Active);
            stored.Type.Should().Be(separate.Type);
            stored.FactsJson.Should().Be(separate.FactsJson);
        }

        _store.Live.Should().HaveCount(4);
    }

    [Fact]
    public async Task Dry_run_plan_changes_no_memory_rows_and_only_refreshes_stale_embeddings()
    {
        var refresh = Seed(SessionAmountRefresh(), Shared(1));
        var rules = Seed(RefreshRules(), Shared(2), modelKey: "old:embed");
        var reuse = Seed(SessionReuse(), Shared(3));
        var changes = Seed(
            Knowledge(MemoryType.Discovery, "Changed 2 file(s) in the payments resolvers",
                "PaymentMutations.cs and PaymentMutationsTests.cs were edited.", "Payments resolvers"),
            Axis(6));
        var allFacts = new[] { refresh, rules, reuse }.SelectMany(Facts).ToList();
        _chat.Answer = user => PaymentsCanonical(user, allFacts, refresh, rules, reuse);
        _embeddings.Map(rules.Title, Shared(2));
        var before = Snapshot();
        var evidenceBefore = _store.EvidenceCount;

        var plan = await PlanAsync();

        Snapshot().Should().Be(before);
        _store.EvidenceCount.Should().Be(evidenceBefore);
        plan.Groups.Should().ContainSingle().Which.Canonicals.Should().ContainSingle();
        plan.Stats.LiveKnowledge.Should().Be(4);
        plan.Stats.Reembedded.Should().Be(1);
        _store.VectorOf(rules.Id)!.ModelKey.Should().Be(ModelKey);
        var reclassified = plan.ReclassifiedEvents.Should().ContainSingle().Subject;
        reclassified.Id.Should().Be(changes.Id);
        reclassified.Reason.Should().Be("file-change summary");

        var roundTripped = ReconsolidationReport.FromJson(plan.ToJson());
        roundTripped.Groups.Single().Canonicals.Single().Facts.Should().Equal(plan.Groups.Single().Canonicals.Single().Facts);
        roundTripped.Groups.Single().Sources.Should().Equal(plan.Groups.Single().Sources);
    }

    [Fact]
    public async Task Apply_revalidates_and_skips_a_group_whose_source_changed_since_the_plan()
    {
        var refresh = Seed(SessionAmountRefresh(), Shared(1));
        var rules = Seed(RefreshRules(), Shared(2));
        var reuse = Seed(SessionReuse(), Shared(3));
        var allFacts = new[] { refresh, rules, reuse }.SelectMany(Facts).ToList();
        _chat.Answer = user => PaymentsCanonical(user, allFacts, refresh, rules, reuse);
        var plan = await PlanAsync();

        var reinforced = _store.Get(rules.Id);
        reinforced.LastReinforcedAt += TimeSpan.FromMinutes(1);
        reinforced.ReinforcementCount++;
        await _store.UpdateAsync(reinforced, null, null, [], CancellationToken.None);

        var applied = await ApplyAsync(plan);

        applied.Outcome!.AppliedGroups.Should().BeEmpty();
        applied.Outcome.SkippedGroups.Should().ContainSingle().Which.Reason.Should().Contain(rules.Id.ToString());
        _store.All.Should().HaveCount(3);
        _store.Live.Should().HaveCount(3);
    }

    [Fact]
    public async Task Excluded_groups_are_not_applied()
    {
        var npmSandbox = Seed(NpmSandboxInstall(), Shared(1));
        var npmE2e = Seed(NpmE2eInstall(), Shared(2));
        var refresh = Seed(SessionAmountRefresh(), Shared(3));
        var rules = Seed(RefreshRules(), Shared(4));
        var reuse = Seed(SessionReuse(), Shared(5));
        var paymentFacts = new[] { refresh, rules, reuse }.SelectMany(Facts).ToList();
        _chat.Answer = user => user.Contains("IsAmountRefreshRequired", StringComparison.Ordinal)
            ? PaymentsCanonical(user, paymentFacts, refresh, rules, reuse)
            : NpmCanonical(user, npmSandbox, npmE2e);
        _embeddings.Map("", Axis(7));

        var plan = await PlanAsync();

        plan.Groups.Should().HaveCount(2);
        var npmGroup = plan.Groups.Single(g => g.Sources.Any(s => s.Id == npmSandbox.Id));
        var paymentGroup = plan.Groups.Single(g => g.Sources.Any(s => s.Id == refresh.Id));

        var applied = await ApplyAsync(plan, exclude: [npmGroup.Index]);

        applied.Outcome!.ExcludedGroups.Should().Equal(npmGroup.Index);
        applied.Outcome.AppliedGroups.Should().Equal(paymentGroup.Index);
        _store.Get(npmSandbox.Id).Status.Should().Be(MemoryStatus.Active);
        _store.Get(npmE2e.Id).Status.Should().Be(MemoryStatus.Active);
        _store.Get(refresh.Id).Status.Should().Be(MemoryStatus.Superseded);
        _store.Live.Should().HaveCount(3);
    }

    [Fact]
    public async Task Canonical_that_drops_a_fact_makes_the_group_ambiguous_and_it_is_never_applied()
    {
        var refresh = Seed(SessionAmountRefresh(), Shared(1));
        var rules = Seed(RefreshRules(), Shared(2));
        var reuse = Seed(SessionReuse(), Shared(3));
        const string dropped = "Same upsert path is used in expired re‑arm logic";
        var keptFacts = new[] { refresh, rules, reuse }.SelectMany(Facts).Where(f => f != dropped).ToList();
        _chat.Answer = user => PaymentsCanonical(user, keptFacts, refresh, rules, reuse);

        var plan = await PlanAsync();

        var group = plan.Groups.Should().ContainSingle().Subject;
        group.Ambiguous.Should().BeTrue();
        group.Note.Should().Contain("drops a fact").And.Contain(dropped);
        plan.Stats.AmbiguousGroups.Should().Be(1);
        plan.Untouched.Should().Be(3);

        var applied = await ApplyAsync(plan);

        applied.Outcome!.SkippedGroups.Should().Equal(new SkippedGroup(group.Index, "ambiguous"));
        _store.All.Should().HaveCount(3);
        _store.Live.Should().HaveCount(3);
    }

    [Fact]
    public async Task Unparseable_answer_leaves_the_group_ambiguous()
    {
        Seed(SessionAmountRefresh(), Shared(1));
        Seed(RefreshRules(), Shared(2));
        _chat.Answer = _ => "These two memories look related to me.";

        var plan = await PlanAsync();

        var group = plan.Groups.Should().ContainSingle().Subject;
        group.Ambiguous.Should().BeTrue();
        group.Note.Should().Contain("no recognizable sections");
        group.Canonicals.Should().BeEmpty();
    }

    [Fact]
    public async Task Contradiction_the_code_settles_supersedes_the_refuted_side()
    {
        var (warning, information) = SeedLogLevelPair();
        _codeVerifier.Check = (_, symbols) => symbols.Contains("UnauthStartPaymentSessionV2")
            ? new CodeCheck(CodeCheckResult.Refuted, "PaymentMutations.cs logs both mismatches with LogWarning")
            : new CodeCheck(CodeCheckResult.Verified, "PaymentMutations.cs calls LogWarning");
        var confidenceBefore = warning.Confidence;

        var plan = await PlanAsync();

        var contradiction = plan.Groups.Should().ContainSingle().Which.Contradictions.Should().ContainSingle().Subject;
        contradiction.CodeCheck!.WinnerId.Should().Be(warning.Id);
        plan.Stats.Contradictions.Should().Be(1);

        await ApplyAsync(plan, resolve: true);

        var loser = _store.Get(information.Id);
        loser.Status.Should().Be(MemoryStatus.Superseded);
        loser.SupersededById.Should().Be(warning.Id);
        _store.EvidenceFor(information.Id).Should().Contain(e => e.Relation == EvidenceRelation.Refuted);

        var winner = _store.Get(warning.Id);
        winner.Status.Should().Be(MemoryStatus.Active);
        winner.Confidence.Should().BeGreaterThan(confidenceBefore);
        _store.EvidenceFor(warning.Id).Should().Contain(e => e.Relation == EvidenceRelation.Verified)
            .And.Contain(e => e.Relation == EvidenceRelation.Superseded && e.SourceMemoryId == information.Id);
    }

    [Fact]
    public async Task Contradiction_without_a_code_verdict_marks_both_sides_contested()
    {
        var (warning, information) = SeedLogLevelPair();

        var plan = await PlanAsync();

        plan.Groups.Single().Contradictions.Single().CodeCheck!.WinnerId.Should().BeNull();

        await ApplyAsync(plan, resolve: true);

        AssertContested(warning, information);
    }

    [Fact]
    public async Task Scheduled_apply_only_flags_a_contradiction_even_when_the_code_settles_it()
    {
        var (warning, information) = SeedLogLevelPair();
        _codeVerifier.Check = (_, symbols) => symbols.Contains("UnauthStartPaymentSessionV2")
            ? new CodeCheck(CodeCheckResult.Refuted, null)
            : new CodeCheck(CodeCheckResult.Verified, null);

        var plan = await PlanAsync();
        await ApplyAsync(plan, resolve: false);

        AssertContested(warning, information);
    }

    [Fact]
    public async Task Change_summaries_stored_as_knowledge_are_reclassified_as_events_on_apply()
    {
        var summary = Seed(Knowledge(MemoryType.Discovery, "Changed 3 files in the refund service",
            "RefundPolicy.cs, RefundJob.cs and RefundJobTests.cs were edited.", "Refund service"), Axis(1));
        var changeLog = Seed(Knowledge(MemoryType.Discovery, "Updated the refund retry job",
            "The refund retry job now runs hourly.", "Refund jobs"), Axis(2));
        var stale = Seed(Knowledge(MemoryType.Discovery, "Removed the legacy ledger export",
            "The nightly ledger export was deleted.", "Ledger export"), Axis(3));
        var pinned = Seed(Knowledge(MemoryType.Discovery, "Changed 2 files in the ledger",
            "LedgerWriter.cs was edited.", "Ledger"), Axis(4), pinned: true);
        var explicitMemory = Seed(Knowledge(MemoryType.Discovery, "Changed 4 files for the gateway timeout",
            "PaymentsGatewayClient.cs was edited.", "Payments gateway", source: EvidenceSource.Explicit), Axis(5));
        var withFacts = Seed(Knowledge(MemoryType.Configuration, "Updated webhook signing to HMAC-SHA256",
            "WebhookVerifier signs with HMAC-SHA256.", "Payments webhooks", ["Webhook signatures use HMAC-SHA256"]), Axis(6));
        var contested = Seed(Knowledge(MemoryType.Discovery, "Changed 5 files in the refund export",
            "RefundExport.cs was edited.", "Refund export"), Axis(7));
        contested.Status = MemoryStatus.Contested;
        await _store.UpdateAsync(contested, null, null, [], CancellationToken.None);

        var plan = await PlanAsync();

        plan.ReclassifiedEvents.Select(e => (e.Id, e.Reason)).Should().BeEquivalentTo(new[]
        {
            (summary.Id, "file-change summary"),
            (changeLog.Id, "change-log title without facts"),
            (stale.Id, "change-log title without facts"),
        });

        var touched = _store.Get(stale.Id);
        touched.LastReinforcedAt += TimeSpan.FromMinutes(1);
        await _store.UpdateAsync(touched, null, null, [], CancellationToken.None);

        var applied = await ApplyAsync(plan);

        applied.Outcome!.ReclassifiedEvents.Should().Be(2);
        applied.Outcome.SkippedEvents.Should().Equal(stale.Id);
        foreach (var reclassified in new[] { summary, changeLog })
        {
            _store.Get(reclassified.Id).Type.Should().Be(MemoryType.Event);
            _store.VectorOf(reclassified.Id).Should().BeNull("events are never embedded");
        }

        _store.Get(stale.Id).Type.Should().Be(MemoryType.Discovery);
        _store.Get(pinned.Id).Type.Should().Be(MemoryType.Discovery);
        _store.Get(explicitMemory.Id).Type.Should().Be(MemoryType.Discovery);
        _store.Get(withFacts.Id).Type.Should().Be(MemoryType.Configuration);
        _store.Get(contested.Id).Type.Should().Be(MemoryType.Discovery, "a contested claim is settled, not reclassified away");
    }

    [Fact]
    public async Task Without_a_chat_model_only_near_identical_memories_merge_by_fact_union()
    {
        var refresh = Seed(SessionAmountRefresh(), Axis(0));
        var rules = Seed(RefreshRules(), Axis(0));
        var reuse = Seed(SessionReuse(), Shared(3));
        _embeddings.Map("", Axis(7));

        var plan = await PlanAsync();

        var group = plan.Groups.Should().ContainSingle().Subject;
        group.Note.Should().StartWith("offline");
        group.Sources.Should().HaveCount(3, "the third memory is related but not a near-duplicate");
        var canonical = group.Canonicals.Should().ContainSingle().Subject;
        canonical.SourceIds.Should().BeEquivalentTo(new[] { refresh.Id, rules.Id });
        canonical.Title.Should().Be(rules.Title, "the most recently reinforced of equally confident sources keeps its wording");
        canonical.Facts.Should().Contain(Facts(refresh)).And.Contain(Facts(rules));
        plan.Stats.UsedModel.Should().BeFalse();
        _chat.Calls.Should().BeEmpty();

        await ApplyAsync(plan);

        _store.Get(reuse.Id).Status.Should().Be(MemoryStatus.Active);
        _store.Live.Should().HaveCount(2);
    }

    [Fact]
    public async Task Oversized_component_is_split_into_groups_of_at_most_the_group_size()
    {
        for (var i = 0; i < 10; i++)
        {
            Seed(Knowledge(MemoryType.Fact, $"RefundPolicy rule {i}", $"RefundPolicy.cs applies refund rule {i}.", $"Refund rule {i}",
                [$"Refund rule {i} is enforced by RefundPolicy.cs"]), Shared(1 + (i % 7)));
        }

        _chat.Answer = _ => NoneSections;

        var plan = await PlanAsync();

        plan.Groups.Should().BeEmpty("a group the model leaves untouched is not reported");
        _chat.Calls.Should().HaveCount(2);
        _chat.Calls.Select(c => SourceTitles(c).Count).Should().AllSatisfy(n => n.Should().BeLessThanOrEqualTo(MemoryReconsolidator.MaxGroupSize));
        _chat.Calls.Sum(c => SourceTitles(c).Count).Should().Be(10);
    }

    private Task<ReconsolidationReport> PlanAsync() => _sut.PlanAsync(_projectId, CancellationToken.None);

    private Task<ReconsolidationReport> ApplyAsync(ReconsolidationReport plan, IReadOnlyCollection<int>? exclude = null, bool resolve = false) =>
        _sut.ApplyAsync(_projectId, plan, new ReconsolidationApplyOptions(exclude ?? [], resolve), CancellationToken.None);

    private (DomainMemory Warning, DomainMemory Information) SeedLogLevelPair()
    {
        var warning = Seed(Knowledge(
            MemoryType.Behavior,
            "PaymentMutations logs for amount mismatch upgraded to LogWarning",
            "In src/dotnet/domain_api/Payments.DomainAppApi.GqlSvc/Gql/Resolvers/Payments/PaymentMutations.cs, the two log statements that previously used `logger.LogInformation` for “Checkout session amount does not match the guarantor balance” and “Input pay amount does not match the checkout session amount” have been changed to `logger.LogWarning`. This upgrade reflects that these mismatches represent client‑side errors that should surface as warnings rather than informational messages.",
            "Logging",
            ["LogWarning called for Checkout session amount mismatch and Input pay amount mismatch.", "Previously these were LogInformation."],
            filesModified: ["src/dotnet/domain_api/Payments.DomainAppApi.GqlSvc/Gql/Resolvers/Payments/PaymentMutations.cs"]), Shared(1));
        var information = Seed(Knowledge(
            MemoryType.Issue,
            "Log level mismatch for balance mismatch checks in UnauthStartPaymentSessionV2",
            "In `src/dotnet/domain_api/Payments.DomainAppApi.GqlSvc/Gql/Resolvers/Payments/PaymentMutations.cs`, the log statements that emit when the checkout session amount does not match the guarantor balance (lines ~912) or when an optional input amount differs from the session amount (lines ~938) use `logger.LogInformation`. The PAY‑15170 requirements explicitly state “Log both cases. Write a warning log whenever either check fails.” A future patch should replace these two `LogInformation` calls with `LogWarning`.",
            "Logging",
            ["mismatch logs use LogInformation instead of LogWarning", "two calls in UnauthStartPaymentSessionV2: one for balance mismatch, one for input amount mismatch"],
            filesRead: ["src/dotnet/domain_api/Payments.DomainAppApi.GqlSvc/Gql/Resolvers/Payments/PaymentMutations.cs"]), Shared(2));
        _chat.Answer = _ => "CONTRADICTIONS:\n- 1|2|one says the mismatch logs use LogWarning, the other LogInformation\nKEEP_SEPARATE: none\n";
        return (warning, information);
    }

    private void AssertContested(DomainMemory a, DomainMemory b)
    {
        foreach (var (memory, partner) in new[] { (a, b), (b, a) })
        {
            _store.Get(memory.Id).Status.Should().Be(MemoryStatus.Contested);
            _store.EvidenceFor(memory.Id).Should().Contain(e => e.Relation == EvidenceRelation.Contradicted && e.SourceMemoryId == partner.Id);
        }

        _store.All.Should().HaveCount(2);
    }

    private DomainMemory Seed(MemoryCandidate candidate, float[] vector, string modelKey = ModelKey, bool pinned = false)
    {
        _clock = _clock.AddMinutes(1);
        var sessionId = Guid.NewGuid();
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
            Pinned = pinned,
            CreatedFromSessionId = sessionId,
            CreatedAt = _clock,
            LastReinforcedAt = _clock,
        };
        var evidence = new MemoryEvidence
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            MemoryId = memory.Id,
            Relation = EvidenceRelation.Created,
            Source = candidate.Source,
            SessionId = sessionId,
            ObservedAt = _clock,
        };
        ConfidenceModel.Recompute(memory, [evidence]);
        var entities = EntityExtractor.Extract(candidate)
            .Select(e => new MemoryEntity { ProjectId = _projectId, MemoryId = memory.Id, Key = e.Key, Display = e.Display });

        _store.Add(memory, new MemoryVector(modelKey, vector), entities, [evidence]);
        return _store.Get(memory.Id);
    }

    private string Snapshot() => JsonSerializer.Serialize(_store.All.OrderBy(m => m.Id));

    private static string PaymentsCanonical(string user, IEnumerable<string> facts, params DomainMemory[] sources) =>
        Canonical(
            user,
            MemoryType.Behavior,
            "CheckoutSessionV1Manager reuses a checkout session only while its amount is current",
            "Checkout session refresh",
            "CheckoutSessionV1Manager returns a stored session only when it is not expired and IsAmountRefreshRequired is false; otherwise one conditional upsert refreshes the amount to the balance token value.",
            facts,
            ["CheckoutSessionV1Manager", "IsAmountRefreshRequired", "LockTimeoutMinutes"],
            ["src/dotnet/domain_api/Payments.DomainAppApi.Svc/CheckoutSessionV1Manager.cs"],
            sources);

    private static string NpmCanonical(string user, DomainMemory npmSandbox, DomainMemory npmE2e) =>
        Canonical(
            user,
            MemoryType.Procedure,
            "npm installs from the sandbox need an Artifactory workaround",
            "npm install workaround",
            "Artifactory NpmExternal rejects the sandbox with 401, so installs run from a Linux-filesystem scratch copy with the repository .npmrc and the offline cache.",
            Facts(npmSandbox).Concat(Facts(npmE2e)),
            ["NpmExternal", "package.json", "package-lock.json"],
            [],
            npmSandbox,
            npmE2e);

    /// <summary>A CANONICAL block naming <paramref name="sources"/> by their numbers in <paramref name="user"/>, then empty other sections.</summary>
    private static string Canonical(
        string user, MemoryType type, string title, string topic, string body, IEnumerable<string> facts,
        IEnumerable<string> entities, IEnumerable<string> filesRead, params DomainMemory[] sources)
    {
        var titles = SourceTitles(user);
        var sb = new StringBuilder()
            .AppendLine("CANONICAL")
            .Append("TYPE: ").AppendLine(type.ToString())
            .Append("TITLE: ").AppendLine(title)
            .Append("TOPIC: ").AppendLine(topic)
            .AppendLine("BODY:")
            .AppendLine(body)
            .AppendLine("FACTS:");
        foreach (var fact in facts)
        {
            sb.Append("- ").AppendLine(fact);
        }

        sb.Append("ENTITIES: ").AppendLine(string.Join(", ", entities));
        sb.AppendLine("FILES_READ:");
        foreach (var file in filesRead)
        {
            sb.Append("- ").AppendLine(file);
        }

        sb.Append("SOURCES: ").AppendLine(string.Join(", ", sources.Select(s => titles.IndexOf(s.Title) + 1)));
        sb.AppendLine("END_CANONICAL").AppendLine();
        return sb.Append(NoneSections).ToString();
    }

    private static List<string> SourceTitles(string user) =>
        [.. user.Split('\n').Where(l => l.StartsWith("TITLE: ", StringComparison.Ordinal)).Select(l => l["TITLE: ".Length..].TrimEnd('\r'))];

    private static MemoryCandidate Knowledge(
        MemoryType type,
        string title,
        string body,
        string topic,
        IReadOnlyList<string>? facts = null,
        EvidenceSource source = EvidenceSource.EventBatch,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null) =>
        new(type, title, body, source, facts ?? [], [], filesRead ?? [], filesModified ?? [], topic);

    private static MemoryCandidate SessionAmountRefresh() => Knowledge(
        MemoryType.Behavior,
        "CheckoutSessionV1Manager refreshes session amount on reuse",
        "In CheckoutSessionV1Manager.IsAmountRefreshRequired (src/dotnet/domain_api/Payments.DomainAppApi.Svc/CheckoutSessionV1Manager.cs line 694) a single upsert logic is used to both expire and re‑arm a checkout session. The method requires that the balance token is non‑empty, an input amount is present, the amount or currency differs (comparing currency codes with OrdinalIgnoreCase), the session status is not Completed, and the session is not locked by a recent InProgress state within LockTimeoutMinutes. This logic satisfies the PAY‑15170 requirement that the session amount stays current on reuse.",
        "Session amount refresh",
        [
            "IsAmountRefreshRequired checks balanceToken non‑empty, input amount !null, amount/currency differ, status != Completed, not locked by recent InProgress",
            "On true, upsert updates session amount to balance token value",
            "Same upsert path is used in expired re‑arm logic",
        ],
        filesRead: ["src/dotnet/domain_api/Payments.DomainAppApi.Svc/CheckoutSessionV1Manager.cs"]);

    private static MemoryCandidate RefreshRules() => Knowledge(
        MemoryType.Behavior,
        "payments_svc IsAmountRefreshRequired session refresh rules",
        "The payments_svc refresh logic implements `IsAmountRefreshRequired` which updates a checkout session’s amount only when all of the following are true: a balance token is set, an amount exists, the amount or currency differs (case‑insensitive comparison), the session status is not Completed, and the session is not in an InProgress state within `LockTimeoutMinutes`. On a refresh, the session’s Updated timestamp is set to now, `VersionStamp` is incremented, the session is re‑signed, and a conditional upsert is performed.",
        "payments_svc refresh logic",
        [
            "IsAmountRefreshRequired requires BalanceToken, amount, differing amount or currency, status ≠ Completed, and not InProgress within LockTimeoutMinutes.",
            "On refresh: set Updated=now, increment VersionStamp, re‑sign, conditional upsert.",
            "Expired sessions are re‑armed +15 minutes.",
            "Complete locks InProgress then upserts Completed with CDE result amount.",
        ]);

    private static MemoryCandidate SessionReuse() => Knowledge(
        MemoryType.Behavior,
        "CheckoutSession reuse only when not expired and amount unchanged",
        "In `Payments.PaymentsSvc.Api/CheckoutSessionV1Manager.cs`, when handling a new payment session the existing session stored under the same idempotency token is returned only if it has not expired (`IsExpired` is false) and the incoming `checkoutSessionInput` does not require an amount refresh (`IsAmountRefreshRequired` is false). If either condition fails a new session is created.",
        "CheckoutSession reuse",
        [
            "Reuse occurs only when `!isExpired && !isAmountRefreshRequired`.",
            "`IsAmountRefreshRequired` takes the existing DB model and the new input to decide if balance changes need a new session.",
        ]);

    private static MemoryCandidate NpmSandboxInstall() => Knowledge(
        MemoryType.Procedure,
        "npm installs in sandbox: Artifactory 401 workaround and E2E lock drift",
        "Artifactory NpmExternal returns 401 from the sandbox, so npm ci for src/typescript/frontend_apps/guarantor_exp and __e2e_testing/guarantor_exp fails. Workaround: check each package-lock integrity against ~/.npm/_cacache/content-v2/<algo>/<hex[:2]>/<hex[2:4]>/<rest>, download missing tarballs from registry.npmjs.org using the path after /NpmExternal/ in resolved, verify the hash, npm cache add, then npm ci --offline --ignore-scripts. Run the install on the Linux filesystem (copy app to /tmp mirroring src/typescript/frontend_apps/<app> and src/graphql so codegen.ts's relative schema path resolves) — on /mnt/c extraction exceeds 20 minutes.",
        "Frontend local verification",
        [
            "Artifactory NpmExternal returns 401 from the Claude sandbox",
            "npm cache presence must be checked by integrity content path, not cache index",
            "npm ci on /mnt/c for guarantor_exp exceeds 20 minutes; use Linux FS copy",
            "__e2e_testing/guarantor_exp package-lock.json drifts from package.json on develop (faker, prettier, fsevents)",
        ]);

    private static MemoryCandidate NpmE2eInstall() => Knowledge(
        MemoryType.Procedure,
        "E2E test npm install workaround for private registry",
        "The background `npm ci` in the E2E worktree failed with E401 when trying to download `prettier`, due to a lock drift against the private Artifactory registry. The fix is to create a separate scratch directory `/tmp/e2e-verify`, copy `package.json` and `package-lock.json` into it, and run `npm install --userconfig <repo‑.npmrc> --registry https://artifactory.phtools.org/artifactory/api/npm/NpmExternal/` from that directory. The resulting `node_modules` is then symlinked into the E2E worktree (`node_modules -> /tmp/e2e-verify/node_modules`).",
        "E2E test setup",
        [
            "Background `npm ci` E401 failed because of private registry access to `prettier`.",
            "Scratch dir `/tmp/e2e-verify` with `--userconfig <repo>.npmrc` installs 691 packages successfully.",
            "`node_modules` symlinked into E2E worktree; must remain git‑ignored.",
            "No auth token is stored in repository files.",
        ]);

    private static MemoryCandidate GitMultiPackIndex() => Knowledge(
        MemoryType.Procedure,
        "Fixed corrupted Git multi-pack-index and verified object integrity",
        "A corrupted multi‑pack‑index at `.git/objects/pack/multi-pack-index` caused `git cat‑file` failures. The fix involved moving the corrupt index to `/tmp/multi-pack-index.corrupt-20260928`, running `git multi-pack-index write`, and immediately verifying with `git multi-pack-index verify` (exit 0).",
        "git maintenance",
        [
            "Corrupted index located at `.git/objects/pack/multi-pack-index`",
            "Recovery commands: `git multi-pack-index write` and `git multi-pack-index verify`",
        ],
        filesRead: [".git/objects/pack/multi-pack-index", ".git/objects/pack/pack-8ee5232e76ce5bebe78c2c046f1cdfb30d74e241.pack"]);

    private static MemoryCandidate BillsGqlUrl() => Knowledge(
        MemoryType.Configuration,
        "AWS_BILLS_GQL_URL config location and pattern",
        "The patient‑bills GraphQL endpoint URL is defined in the CI configuration files. In .gl-ci-autopay.yml line 97 and .gl-ci-bills.yml line 32 it is set as `https://pmts-patient-bills-${AWS_ENV_PREFIX}-ue1.test.phreesia.app/gql`. The same env variable AWS_BILLS_GQL_URL is consumed in __e2e_testing/bills/support/actions.ts to build request URLs.",
        "GraphQL API config",
        [
            "AWS_BILLS_GQL_URL set in .gl-ci-autopay.yml:97 to https://pmts-patient-bills-${AWS_ENV_PREFIX}-ue1.test.phreesia.app/gql",
            "AWS_BILLS_GQL_URL set in .gl-ci-bills.yml:32 to https://pmts-patient-bills-tst-ue1.test.phreesia.app/gql",
            "Actions.ts uses env.AWS_BILLS_GQL_URL to post custom GraphQL requests",
        ],
        filesRead: [".gl-ci-autopay.yml", ".gl-ci-bills.yml", "__e2e_testing/bills/support/actions.ts"]);

    private static MemoryCandidate PatientBillGsi() => Knowledge(
        MemoryType.Architecture,
        "Patient bill GSI key construction in repository",
        "In mgmt_api/shared/pkg/repositories/patient_bill_repository.go the query for patient bills uses the GSI2 index. The repository calls dynamotablekeys.FormatPatientBillGSI2PK(practiceId, patientId) to build the GSI2PK and dynamotablekeys.FormatPatientBillGSI2SK(practiceId, \"\") for the GSI2SK.",
        "Patient bill repository",
        [
            "patient_bill_repository.go uses FormatPatientBillGSI2PK for GSI2PK",
            "patient_bill_repository.go uses FormatPatientBillGSI2SK for GSI2SK",
            "GSI2SK is constructed with practiceId and an empty string",
        ],
        filesModified: ["mgmt_api/shared/pkg/repositories/patient_bill_repository.go"]);

    private static IReadOnlyList<string> Facts(DomainMemory memory) =>
        JsonSerializer.Deserialize<List<string>>(memory.FactsJson ?? "[]") ?? [];

    private static float[] Axis(int index)
    {
        var vector = new float[Dimensions];
        vector[index] = 1f;
        return vector;
    }

    /// <summary>Unit vectors sharing one component: any two distinct ones are at cosine 0.75, above the
    /// grouping floor but below the offline duplicate threshold.</summary>
    private static float[] Shared(int index)
    {
        var vector = new float[Dimensions];
        vector[0] = (float)Math.Sqrt(0.75);
        vector[index] = 0.5f;
        return vector;
    }

    /// <summary>Offline ("noop") until <see cref="Answer"/> is set; records every user prompt.</summary>
    private sealed class ReconsolidationChat : IChatClient
    {
        public List<string> Calls { get; } = [];

        public Func<string, string>? Answer { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var list = messages.ToList();
            if (list.First(m => m.Role == ChatRole.System).Text != ReconsolidationPrompt.SystemPrompt || Answer is not { } answer)
            {
                throw new InvalidOperationException("Unexpected prompt.");
            }

            var user = list.First(m => m.Role == ChatRole.User).Text;
            Calls.Add(user);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(user))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(Answer is null ? "noop" : "fake", null, "model") : null;

        public void Dispose()
        {
        }
    }
}
