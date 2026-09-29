using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers.Abstractions;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Application.Settings;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using DomainFile = Context.Domain.Entities.File;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

/// <summary>
/// Tests for <see cref="ContextEngine"/>. <see cref="ContextPackBuilder"/>
/// (Context.Retrieval) and <see cref="SessionDeliveryCache"/> are constructed for real, with only
/// their own dependency (<see cref="ISearchEngine"/>) mocked; every repository/queue/traversal
/// dependency of <see cref="ContextEngine"/> itself is a Moq mock.
/// </summary>
public sealed class ContextEngineTests
{
    private readonly Mock<ISearchEngine> _searchEngine = new();
    private readonly Mock<IJobQueue> _jobQueue = new();
    private readonly Mock<IGraphTraversal> _graphTraversal = new();
    private readonly Mock<IGraphCacheProvider> _graphCacheProvider = new();
    private readonly Mock<IProjectRepository> _projectRepository = new();
    private readonly Mock<IFileRepository> _fileRepository = new();
    private readonly Mock<ISymbolRepository> _symbolRepository = new();
    private readonly Mock<IChunkRepository> _chunkRepository = new();
    private readonly Mock<IMemoryRepository> _memoryRepository = new();
    private readonly Mock<ISessionRepository> _sessionRepository = new();
    private readonly Mock<IProjectSettingsService> _projectSettingsService = new();
    private readonly Mock<IMemoryConsolidator> _memoryConsolidator = new();
    private readonly Mock<IMemoryStore> _memoryStore = new();
    private readonly Mock<IMemoryVectorIndex> _memoryVectorIndex = new();
    private readonly Mock<ISessionSummaryVectorIndex> _sessionSummaryVectorIndex = new();
    private readonly Mock<IProviderResolver> _providerResolver = new();
    private readonly Mock<IEmbeddingGenerator<string, Embedding<float>>> _embeddingGenerator = new();
    private readonly SessionDeliveryCache _deliveryCache = new();
    private readonly ContextEngine _sut;

    public ContextEngineTests()
    {
        var packBuilder = new ContextPackBuilder(_searchEngine.Object);

        _sut = new ContextEngine(
            packBuilder,
            _jobQueue.Object,
            _graphTraversal.Object,
            _graphCacheProvider.Object,
            _projectRepository.Object,
            _fileRepository.Object,
            _symbolRepository.Object,
            _chunkRepository.Object,
            _memoryRepository.Object,
            _sessionRepository.Object,
            _deliveryCache,
            _projectSettingsService.Object,
            _memoryConsolidator.Object,
            _memoryStore.Object,
            _memoryVectorIndex.Object,
            _sessionSummaryVectorIndex.Object,
            _providerResolver.Object);

        // Neutral defaults so tests only need to configure what they actually exercise.
        _symbolRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Symbol?)null);
        _fileRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainFile?)null);
        _memoryRepository.Setup(r => r.ListByProjectAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[]);
        _memoryRepository.Setup(r => r.ListLiveEntitiesByKeysAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEntity>)[]);
        _projectSettingsService.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectSettingsDto(
                EmbeddingProvider: "noop",
                LlmProvider: "noop",
                OllamaBaseUrl: null,
                OllamaEmbeddingModel: null,
                OllamaChatModel: null,
                ReindexEnabled: false,
                ReindexIntervalMinutes: 60,
                LastReindexScheduledAt: null,
                SessionSummaryIntervalMinutes: 0,
                MinEventsForSessionSummary: 15,
                MaxMemoryNotesPerSection: 20));
        _searchEngine.Setup(s => s.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SearchHit>)[]);

        // Semantic memory search always resolves a provider; default it to no hits so tests exercise
        // lexical ranking unless they configure the vector index.
        _embeddingGenerator.Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 0f })]));
        _providerResolver.Setup(p => p.GetEmbeddingGeneratorAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_embeddingGenerator.Object);
        _memoryVectorIndex.Setup(v => v.SearchAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((IReadOnlyList<(Guid MemoryId, double Score)>)[]);
    }

    private static DomainMemory MakeMemory(
        Guid projectId, MemoryType type, string title, string body,
        double confidence = 0.7, bool pinned = false, DateTimeOffset? lastReinforcedAt = null, Guid? supersededById = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DomainMemory
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Type = type,
            Title = title,
            Body = body,
            Confidence = confidence,
            Pinned = pinned,
            LastReinforcedAt = lastReinforcedAt ?? now,
            CreatedAt = now,
            Status = supersededById is null ? MemoryStatus.Active : MemoryStatus.Superseded,
            SupersededById = supersededById
        };
    }

    // ---- 1. SearchContextAsync merges ranked DomainMemory records into historical notes / open issues ----

    [Fact]
    public async Task SearchContextAsync_merges_ranked_memories_into_historical_notes_and_open_issues()
    {
        var projectId = Guid.NewGuid();

        var decision = MakeMemory(projectId, MemoryType.Decision, "Use SQLite", "Because it is simple", confidence: 0.9, pinned: true);
        var issue = MakeMemory(projectId, MemoryType.Issue, "Flaky test", "SQLite lock contention fails CI", confidence: 0.8);
        var unrelated = MakeMemory(projectId, MemoryType.Fact, "Dashboard theme", "Dark by default");

        _memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[decision, issue, unrelated]);

        var query = new SearchQuery(projectId, "sqlite", null, TokenBudget: 5000);

        var pack = await _sut.SearchContextAsync(query, CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle()
            .Which.Should().Be("[Decision · conf 0.90 · 1 source] Use SQLite: Because it is simple");
        pack.OpenIssues.Should().ContainSingle()
            .Which.Should().Be("[Issue · conf 0.80 · 1 source] Flaky test: SQLite lock contention fails CI");
    }

    [Fact]
    public async Task SearchContextAsync_returns_empty_memory_sections_when_project_has_no_memories()
    {
        var projectId = Guid.NewGuid();
        var query = new SearchQuery(projectId, "anything", null, TokenBudget: 5000);

        var pack = await _sut.SearchContextAsync(query, CancellationToken.None);

        pack.HistoricalNotes.Should().BeEmpty();
        pack.OpenIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchContextAsync_respects_runtime_max_memory_notes_per_section()
    {
        var projectId = Guid.NewGuid();
        _projectSettingsService.Setup(s => s.GetAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectSettingsDto(
                EmbeddingProvider: "noop",
                LlmProvider: "noop",
                OllamaBaseUrl: null,
                OllamaEmbeddingModel: null,
                OllamaChatModel: null,
                ReindexEnabled: false,
                ReindexIntervalMinutes: 60,
                LastReindexScheduledAt: null,
                SessionSummaryIntervalMinutes: 0,
                MinEventsForSessionSummary: 15,
                MaxMemoryNotesPerSection: 1));

        var memories = new List<DomainMemory>
        {
            MakeMemory(projectId, MemoryType.Decision, "Decision A", "Body A", confidence: 0.2),
            MakeMemory(projectId, MemoryType.Decision, "Decision B", "Body B", confidence: 0.9, pinned: true),
            MakeMemory(projectId, MemoryType.Issue, "Issue A", "Issue Body A", confidence: 0.4),
            MakeMemory(projectId, MemoryType.Issue, "Issue B", "Issue Body B", confidence: 0.8)
        };

        _memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

        var query = new SearchQuery(projectId, string.Empty, null, TokenBudget: 5000);

        var pack = await _sut.SearchContextAsync(query, CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle().Which.Should().Contain("Decision B");
        pack.OpenIssues.Should().ContainSingle().Which.Should().Contain("Issue B");
    }

    // ---- 1b. Query-aware memory sections: knowledge for domain questions, events for change history ----

    private void SetupLiveMemories(Guid projectId, params DomainMemory[] memories) =>
        _memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

    private (DomainMemory Canonical, DomainMemory[] Events) SeedTokenRefreshHistory(Guid projectId)
    {
        var canonical = MakeMemory(projectId, MemoryType.Decision, "Token refresh strategy",
            "Refresh tokens rotate on every refresh and expire after 14 days", confidence: 0.8);
        canonical.SourceCount = 6;
        canonical.Topic = "Auth";

        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(1, 6)
            .Select(i =>
            {
                var e = MakeMemory(projectId, MemoryType.Event, $"Adjusted token refresh step {i}",
                    $"Session {i} tweaked how the token refresh works", confidence: 0.9);
                e.CreatedAt = now.AddDays(-i);
                return e;
            })
            .ToArray();

        SetupLiveMemories(projectId, [canonical, .. events]);
        return (canonical, events);
    }

    [Fact]
    public async Task SearchContextAsync_answers_a_domain_question_with_canonical_knowledge_and_no_events()
    {
        var projectId = Guid.NewGuid();
        SeedTokenRefreshHistory(projectId);

        var pack = await _sut.SearchContextAsync(
            new SearchQuery(projectId, "how does token refresh work", null, TokenBudget: 5000), CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle()
            .Which.Should().Be("[Decision · Auth · conf 0.80 · 6 sources] Token refresh strategy: Refresh tokens rotate on every refresh and expire after 14 days");
    }

    [Fact]
    public async Task SearchContextAsync_answers_a_change_history_question_with_events_newest_first()
    {
        var projectId = Guid.NewGuid();
        var (canonical, events) = SeedTokenRefreshHistory(projectId);

        var pack = await _sut.SearchContextAsync(
            new SearchQuery(projectId, "what changed in token refresh recently", null, TokenBudget: 5000), CancellationToken.None);

        var expectedEvents = events.OrderByDescending(e => e.CreatedAt).ToList();
        pack.HistoricalNotes.Should().HaveCount(events.Length + 1);
        for (var i = 0; i < expectedEvents.Count; i++)
        {
            pack.HistoricalNotes[i].Should().StartWith($"[Event · conf 0.90 · 1 source] {expectedEvents[i].Title}:");
        }

        pack.HistoricalNotes[^1].Should().Contain(canonical.Title);
    }

    [Fact]
    public async Task SearchContextAsync_with_an_empty_query_shows_top_knowledge_and_never_events()
    {
        var projectId = Guid.NewGuid();
        var (canonical, _) = SeedTokenRefreshHistory(projectId);

        var pack = await _sut.SearchContextAsync(new SearchQuery(projectId, string.Empty, null, TokenBudget: 5000), CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle().Which.Should().Contain(canonical.Title);
    }

    [Fact]
    public async Task SearchContextAsync_lists_contested_knowledge_after_active_with_a_marker()
    {
        var projectId = Guid.NewGuid();
        var contested = MakeMemory(projectId, MemoryType.Decision, "Cache TTL is 10 minutes", "Set in CacheOptions", confidence: 0.95, pinned: true);
        contested.Status = MemoryStatus.Contested;
        var active = MakeMemory(projectId, MemoryType.Fact, "Cache TTL is 5 minutes", "Set in CacheOptions", confidence: 0.3);
        SetupLiveMemories(projectId, contested, active);

        var pack = await _sut.SearchContextAsync(new SearchQuery(projectId, "cache ttl", null, TokenBudget: 5000), CancellationToken.None);

        pack.HistoricalNotes.Should().HaveCount(2);
        pack.HistoricalNotes[0].Should().Contain(active.Title).And.NotContain("CONTESTED");
        pack.HistoricalNotes[1].Should().StartWith("[Decision · conf 0.95 · 1 source · CONTESTED] Cache TTL is 10 minutes");
    }

    [Fact]
    public async Task SearchContextAsync_appends_facts_when_the_section_budget_allows()
    {
        var projectId = Guid.NewGuid();
        var memory = MakeMemory(projectId, MemoryType.Configuration, "Hub port", "The hub listens on 5173", confidence: 0.5);
        memory.FactsJson = "[\"HUB_PORT defaults to 5173\",\"Override via env\"]";
        SetupLiveMemories(projectId, memory);

        var pack = await _sut.SearchContextAsync(new SearchQuery(projectId, "hub port", null, TokenBudget: 5000), CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle()
            .Which.Should().EndWith("The hub listens on 5173 Facts: HUB_PORT defaults to 5173; Override via env");
    }

    [Fact]
    public async Task SearchContextAsync_matches_memories_through_entities_and_the_vector_index()
    {
        var projectId = Guid.NewGuid();
        var byEntity = MakeMemory(projectId, MemoryType.Decision, "Retry budget", "Three attempts then dead-letter");
        var bySemantics = MakeMemory(projectId, MemoryType.Behavior, "Backoff", "Exponential with jitter");
        var unrelated = MakeMemory(projectId, MemoryType.Fact, "Dashboard theme", "Dark by default");
        SetupLiveMemories(projectId, byEntity, bySemantics, unrelated);

        _memoryRepository.Setup(r => r.ListLiveEntitiesByKeysAsync(projectId, It.Is<IReadOnlyCollection<string>>(k => k.Contains("ticket:phun-123")), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEntity>)[new MemoryEntity { ProjectId = projectId, MemoryId = byEntity.Id, Key = "ticket:phun-123", Display = "PHUN-123" }]);
        _memoryVectorIndex.Setup(v => v.SearchAsync(
                projectId, It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync((IReadOnlyList<(Guid MemoryId, double Score)>)[(bySemantics.Id, 0.7)]);

        var pack = await _sut.SearchContextAsync(new SearchQuery(projectId, "PHUN-123", null, TokenBudget: 5000), CancellationToken.None);

        pack.HistoricalNotes.Should().HaveCount(2);
        pack.HistoricalNotes.Should().Contain(n => n.Contains(byEntity.Title)).And.Contain(n => n.Contains(bySemantics.Title));
    }

    [Fact]
    public async Task SearchContextAsync_shows_every_issue_for_a_pending_work_query_but_only_matching_issues_otherwise()
    {
        var projectId = Guid.NewGuid();
        var sqliteIssue = MakeMemory(projectId, MemoryType.Issue, "SQLite lock", "Writers contend");
        var uiIssue = MakeMemory(projectId, MemoryType.Issue, "Panel flicker", "Angular change detection");
        SetupLiveMemories(projectId, sqliteIssue, uiIssue);

        var pending = await _sut.SearchContextAsync(new SearchQuery(projectId, "what's still pending for sqlite", null, TokenBudget: 5000), CancellationToken.None);
        var focused = await _sut.SearchContextAsync(new SearchQuery(projectId, "sqlite", null, TokenBudget: 5000), CancellationToken.None);

        pending.OpenIssues.Should().HaveCount(2);
        focused.OpenIssues.Should().ContainSingle().Which.Should().Contain(sqliteIssue.Title);
    }

    // ---- 2. Session-scoped delivery cache ----

    [Fact]
    public async Task SearchContextAsync_replaces_recurring_node_with_already_shown_marker_on_second_call_in_same_session()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        var hit = new SearchHit(nodeId, "Symbol", "Foo.Bar", 1.0, "original snippet content");
        _searchEngine.Setup(s => s.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SearchHit>)[hit]);

        var query = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionId: sessionId);

        var first = await _sut.SearchContextAsync(query, CancellationToken.None);
        var second = await _sut.SearchContextAsync(query, CancellationToken.None);

        first.PrimaryHits.Should().ContainSingle().Which.Snippet.Should().Be("original snippet content");

        var secondSnippet = second.PrimaryHits.Should().ContainSingle().Which.Snippet;
        secondSnippet.Should().NotBeNull();
        secondSnippet.Should().StartWith("[already shown to this session at ");
        secondSnippet.Should().Contain(hit.Kind);
        secondSnippet.Should().Contain($"\"{hit.Label}\"");
        secondSnippet.Should().Contain($"node {nodeId}");
        secondSnippet.Should().Contain("call expand to see the full content again");
    }

    [Fact]
    public async Task SearchContextAsync_does_not_apply_delivery_cache_across_different_sessions()
    {
        var projectId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        var hit = new SearchHit(nodeId, "Symbol", "Foo.Bar", 1.0, "original snippet content");
        _searchEngine.Setup(s => s.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SearchHit>)[hit]);

        var firstQuery = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionId: Guid.NewGuid());
        var secondQuery = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionId: Guid.NewGuid());

        var first = await _sut.SearchContextAsync(firstQuery, CancellationToken.None);
        var second = await _sut.SearchContextAsync(secondQuery, CancellationToken.None);

        first.PrimaryHits.Should().ContainSingle().Which.Snippet.Should().Be("original snippet content");
        second.PrimaryHits.Should().ContainSingle().Which.Snippet.Should().Be("original snippet content");
    }

    [Fact]
    public async Task SearchContextAsync_does_not_apply_delivery_cache_when_query_has_no_session_id()
    {
        var projectId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        var hit = new SearchHit(nodeId, "Symbol", "Foo.Bar", 1.0, "original snippet content");
        _searchEngine.Setup(s => s.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SearchHit>)[hit]);

        var query = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionId: null);

        var first = await _sut.SearchContextAsync(query, CancellationToken.None);
        var second = await _sut.SearchContextAsync(query, CancellationToken.None);

        first.PrimaryHits.Should().ContainSingle().Which.Snippet.Should().Be("original snippet content");
        second.PrimaryHits.Should().ContainSingle().Which.Snippet.Should().Be("original snippet content");
    }

    // ---- 3. GraphImpactAsync delegates to IGraphTraversal.BlastRadiusAsync verbatim ----

    [Fact]
    public async Task GraphImpactAsync_delegates_to_graph_traversal_and_returns_result_verbatim()
    {
        var symbolId = Guid.NewGuid();
        const int maxHops = 3;

        var callerNode = new GraphNode { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Kind = GraphNodeKind.Function, Label = "Caller" };
        var expected = new ImpactSet(
            Callers: [new ImpactNode(callerNode, 1, "Caller -> Foo")],
            Implementers: [],
            CoveringTests: []);

        _graphTraversal
            .Setup(t => t.BlastRadiusAsync(symbolId, maxHops, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _sut.GraphImpactAsync(symbolId, maxHops, CancellationToken.None);

        result.Should().BeSameAs(expected);
        _graphTraversal.Verify(t => t.BlastRadiusAsync(symbolId, maxHops, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- 3b. StartSessionAsync dedupes by claudeSessionId ----

    [Fact]
    public async Task StartSessionAsync_reuses_existing_session_with_the_same_claudeSessionId()
    {
        var projectId = Guid.NewGuid();
        var claudeSessionId = "claude-session-abc";
        var existing = new Session
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ClaudeSessionId = claudeSessionId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = SessionStatus.Active
        };

        _sessionRepository.Setup(r => r.GetByClaudeSessionIdAsync(projectId, claudeSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await _sut.StartSessionAsync(projectId, claudeSessionId, CancellationToken.None);

        result.Should().Be(existing.Id);
        _sessionRepository.Verify(r => r.AddAsync(It.IsAny<Session>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSessionAsync_creates_a_new_session_when_no_existing_row_has_that_claudeSessionId()
    {
        var projectId = Guid.NewGuid();
        var claudeSessionId = "claude-session-new";

        _sessionRepository.Setup(r => r.GetByClaudeSessionIdAsync(projectId, claudeSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Session?)null);
        _sessionRepository.Setup(r => r.AddAsync(It.IsAny<Session>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Session s, CancellationToken _) => s);

        var result = await _sut.StartSessionAsync(projectId, claudeSessionId, CancellationToken.None);

        result.Should().NotBe(Guid.Empty);
        _sessionRepository.Verify(r => r.AddAsync(
            It.Is<Session>(s => s.ProjectId == projectId && s.ClaudeSessionId == claudeSessionId),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StartSessionAsync_never_looks_up_by_claudeSessionId_when_it_is_null()
    {
        var projectId = Guid.NewGuid();
        _sessionRepository.Setup(r => r.AddAsync(It.IsAny<Session>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Session s, CancellationToken _) => s);

        await _sut.StartSessionAsync(projectId, claudeSessionId: null, CancellationToken.None);

        _sessionRepository.Verify(
            r => r.GetByClaudeSessionIdAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSessionAsync_persists_cwd_on_new_session_creation()
    {
        var projectId = Guid.NewGuid();
        _sessionRepository.Setup(r => r.AddAsync(It.IsAny<Session>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Session s, CancellationToken _) => s);

        await _sut.StartSessionAsync(projectId, claudeSessionId: null, CancellationToken.None, cwd: "/repo");

        _sessionRepository.Verify(
            r => r.AddAsync(It.Is<Session>(s => s.Cwd == "/repo"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A background StaleSessionScheduler can end a session that later turns out to still be
    // alive (hook traffic resumes after a network blip, e.g.) -- renewed hook traffic for the
    // same claudeSessionId must un-end it rather than keep handing events to a row stuck Ended.
    [Fact]
    public async Task StartSessionAsync_reactivates_a_reused_session_that_was_previously_Ended()
    {
        var projectId = Guid.NewGuid();
        var claudeSessionId = "claude-session-resumed";
        var existing = new Session
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ClaudeSessionId = claudeSessionId,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-5),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-4),
            EndReason = "stale-timeout",
            Status = SessionStatus.Ended
        };

        _sessionRepository.Setup(r => r.GetByClaudeSessionIdAsync(projectId, claudeSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var result = await _sut.StartSessionAsync(projectId, claudeSessionId, CancellationToken.None);

        result.Should().Be(existing.Id);
        existing.Status.Should().Be(SessionStatus.Active);
        existing.EndedAt.Should().BeNull();
        _sessionRepository.Verify(r => r.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
        _sessionRepository.Verify(r => r.AddAsync(It.IsAny<Session>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- 4. EndSessionAsync enqueues SummarizeSession via IJobQueue ----

    [Fact]
    public async Task EndSessionAsync_enqueues_summarize_session_job_for_the_given_session()
    {
        var sessionId = Guid.NewGuid();
        var session = new Session
        {
            Id = sessionId,
            ProjectId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = SessionStatus.Active
        };

        _sessionRepository.Setup(r => r.GetByIdAsync(session.ProjectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _jobQueue
            .Setup(q => q.EnqueueAsync(JobType.SummarizeSession, It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        await _sut.EndSessionAsync(session.ProjectId, sessionId, CancellationToken.None);

        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.SummarizeSession,
                It.Is<object>(p => p is SummarizeSessionPayload && ((SummarizeSessionPayload)p).SessionId == sessionId),
                session.ProjectId,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        session.Status.Should().Be(SessionStatus.Ended);
        session.EndedAt.Should().NotBeNull();
        _sessionRepository.Verify(r => r.UpdateAsync(session, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EndSessionAsync_persists_the_given_reason_onto_the_session()
    {
        var sessionId = Guid.NewGuid();
        var session = new Session
        {
            Id = sessionId,
            ProjectId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = SessionStatus.Active
        };

        _sessionRepository.Setup(r => r.GetByIdAsync(session.ProjectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _jobQueue
            .Setup(q => q.EnqueueAsync(JobType.SummarizeSession, It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        await _sut.EndSessionAsync(session.ProjectId, sessionId, CancellationToken.None, reason: "stale-timeout");

        session.EndReason.Should().Be("stale-timeout");
    }

    [Fact]
    public async Task EndSessionAsync_throws_when_session_does_not_exist()
    {
        var sessionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        _sessionRepository.Setup(r => r.GetByIdAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync((Session?)null);

        var act = () => _sut.EndSessionAsync(projectId, sessionId, CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        _jobQueue.Verify(
            q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- 4b. Memory extraction batching ----

    [Fact]
    public async Task RecordEventAsync_enqueues_ExtractMemory_for_the_batch_it_claims()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        _sessionRepository.Setup(r => r.ClaimEventsForExtractionAsync(sessionId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(batchId);

        await _sut.RecordEventAsync(projectId, sessionId, SessionEventType.ToolCall, new { tool = "Edit" }, CancellationToken.None);

        _sessionRepository.Verify(r => r.AddEventAsync(It.Is<SessionEvent>(e => e.SessionId == sessionId), It.IsAny<CancellationToken>()), Times.Once);
        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.ExtractMemory,
                It.Is<object>(p => p is ExtractMemoryPayload && ((ExtractMemoryPayload)p).EventBatchId == batchId
                    && ((ExtractMemoryPayload)p).SessionId == sessionId),
                projectId,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordEventAsync_enqueues_nothing_while_the_batch_is_still_filling()
    {
        var sessionId = Guid.NewGuid();
        _sessionRepository.Setup(r => r.ClaimEventsForExtractionAsync(sessionId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        await _sut.RecordEventAsync(Guid.NewGuid(), sessionId, SessionEventType.ToolCall, new { tool = "Read" }, CancellationToken.None);

        _jobQueue.Verify(
            q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EndSessionAsync_flushes_whatever_events_are_left_into_a_final_batch()
    {
        var sessionId = Guid.NewGuid();
        var session = new Session
        {
            Id = sessionId,
            ProjectId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = SessionStatus.Active
        };
        _sessionRepository.Setup(r => r.GetByIdAsync(session.ProjectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _sessionRepository.Setup(r => r.ClaimEventsForExtractionAsync(sessionId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        await _sut.EndSessionAsync(session.ProjectId, sessionId, CancellationToken.None);

        _sessionRepository.Verify(r => r.ClaimEventsForExtractionAsync(sessionId, 1, It.IsAny<CancellationToken>()), Times.Once);
        _jobQueue.Verify(
            q => q.EnqueueAsync(JobType.ExtractMemory, It.IsAny<object>(), session.ProjectId, JobPriority.Session, It.IsAny<CancellationToken>()),
            Times.Once);
        _jobQueue.Verify(
            q => q.EnqueueAsync(JobType.SummarizeSession, It.IsAny<object>(), session.ProjectId, JobPriority.Session, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExtractMemoryFromCompactSummaryAsync_enqueues_the_summary_and_ignores_blank_input()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await _sut.ExtractMemoryFromCompactSummaryAsync(projectId, sessionId, "   ", CancellationToken.None);
        await _sut.ExtractMemoryFromCompactSummaryAsync(projectId, sessionId, "Switched auth to PKCE.", CancellationToken.None);

        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.ExtractMemory,
                It.Is<object>(p => p is ExtractMemoryPayload && ((ExtractMemoryPayload)p).CompactSummary == "Switched auth to PKCE."),
                projectId,
                JobPriority.Session,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _jobQueue.VerifyNoOtherCalls();
    }

    // ---- 5. SearchMemoryAsync / GetMemoryAsync / ForgetMemoryAsync ----

    [Fact]
    public async Task SearchMemoryAsync_forwards_includeSuperseded_to_the_repository()
    {
        var projectId = Guid.NewGuid();
        var active = MakeMemory(projectId, MemoryType.Decision, "Active", "still valid");
        var superseded = MakeMemory(projectId, MemoryType.Decision, "Old", "replaced", supersededById: Guid.NewGuid());

        _memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[active]);
        _memoryRepository.Setup(r => r.ListByProjectAsync(projectId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[active, superseded]);

        var excludingSuperseded = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, CancellationToken.None);
        var includingSuperseded = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: true, CancellationToken.None);

        excludingSuperseded.Should().ContainSingle().Which.Should().BeSameAs(active);
        includingSuperseded.Should().BeEquivalentTo([active, superseded]);
    }

    [Fact]
    public async Task SearchMemoryAsync_ranks_a_pinned_memory_above_a_non_pinned_one_with_equal_confidence_and_age()
    {
        var projectId = Guid.NewGuid();
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-200);

        // Pinned memories skip age decay, so the non-pinned one with the same old timestamp scores lower.
        var pinned = MakeMemory(projectId, MemoryType.Decision, "Pinned", "b", confidence: 0.5, pinned: true, lastReinforcedAt: oldTimestamp);
        var notPinned = MakeMemory(projectId, MemoryType.Decision, "NotPinned", "b", confidence: 0.5, pinned: false, lastReinforcedAt: oldTimestamp);
        SetupLiveMemories(projectId, notPinned, pinned);

        var result = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, CancellationToken.None);

        result.Should().Equal(pinned, notPinned);
    }

    [Fact]
    public async Task SearchMemoryAsync_ranks_higher_confidence_above_lower_confidence_when_otherwise_equal()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var highConfidence = MakeMemory(projectId, MemoryType.Decision, "High", "b", confidence: 0.9, lastReinforcedAt: now);
        var lowConfidence = MakeMemory(projectId, MemoryType.Decision, "Low", "b", confidence: 0.2, lastReinforcedAt: now);
        SetupLiveMemories(projectId, lowConfidence, highConfidence);

        var result = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, CancellationToken.None);

        result.Should().Equal(highConfidence, lowConfidence);
    }

    [Fact]
    public async Task SearchMemoryAsync_returns_only_matching_memories_when_the_query_names_a_subject()
    {
        var projectId = Guid.NewGuid();
        var match = MakeMemory(projectId, MemoryType.Fact, "Migrations", "EF Core migrations run on startup");
        var other = MakeMemory(projectId, MemoryType.Fact, "Dashboard theme", "Dark by default");
        SetupLiveMemories(projectId, match, other);

        var result = await _sut.SearchMemoryAsync(projectId, "migration", includeSuperseded: false, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().BeSameAs(match);
    }

    [Fact]
    public async Task SearchMemoryAsync_puts_knowledge_before_events_unless_the_query_asks_for_change_history()
    {
        var projectId = Guid.NewGuid();
        var evt = MakeMemory(projectId, MemoryType.Event, "SQLite migration shipped", "Moved the SQLite migration to startup", confidence: 0.9);
        var fact = MakeMemory(projectId, MemoryType.Fact, "SQLite migration", "Runs from the SQLite migration runner", confidence: 0.5);
        SetupLiveMemories(projectId, evt, fact);

        var domain = await _sut.SearchMemoryAsync(projectId, "sqlite migration", includeSuperseded: false, CancellationToken.None);
        var history = await _sut.SearchMemoryAsync(projectId, "what changed in sqlite migration recently", includeSuperseded: false, CancellationToken.None);

        domain.Should().Equal(fact, evt);
        history.Should().Equal(evt, fact);
    }

    [Fact]
    public async Task SearchMemoryAsync_filters_by_kind_and_topic()
    {
        var projectId = Guid.NewGuid();
        var authDecision = MakeMemory(projectId, MemoryType.Decision, "JWT", "Short-lived tokens");
        authDecision.Topic = "Auth";
        var authEvent = MakeMemory(projectId, MemoryType.Event, "Rotated keys", "Rotated JWT signing keys");
        authEvent.Topic = "Auth";
        var dbFact = MakeMemory(projectId, MemoryType.Fact, "SQLite", "WAL mode");
        dbFact.Topic = "Storage";
        SetupLiveMemories(projectId, authDecision, authEvent, dbFact);

        var knowledge = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, CancellationToken.None, kind: MemoryKind.Knowledge);
        var events = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, CancellationToken.None, kind: MemoryKind.Event);
        var auth = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, CancellationToken.None, topic: " auth ");

        knowledge.Should().BeEquivalentTo([authDecision, dbFact]);
        events.Should().ContainSingle().Which.Should().BeSameAs(authEvent);
        auth.Should().BeEquivalentTo([authDecision, authEvent]);
    }

    [Fact]
    public async Task SearchMemoryAsync_falls_back_to_lexical_matching_when_embedding_fails()
    {
        var projectId = Guid.NewGuid();
        var match = MakeMemory(projectId, MemoryType.Fact, "Migrations", "EF Core migrations run on startup");
        SetupLiveMemories(projectId, match);
        _providerResolver.Setup(p => p.GetEmbeddingGeneratorAsync(projectId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        var result = await _sut.SearchMemoryAsync(projectId, "migrations", includeSuperseded: false, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().BeSameAs(match);
    }

    [Fact]
    public async Task GetMemoryAsync_returns_evidence_and_entities_only_for_the_owning_project()
    {
        var projectId = Guid.NewGuid();
        var memory = MakeMemory(projectId, MemoryType.Decision, "JWT", "Short-lived tokens");
        var evidence = new MemoryEvidence { Id = Guid.NewGuid(), ProjectId = projectId, MemoryId = memory.Id, ObservedAt = DateTimeOffset.UtcNow };
        var entity = new MemoryEntity { ProjectId = projectId, MemoryId = memory.Id, Key = "symbol:jwtissuer", Display = "JwtIssuer" };
        _memoryRepository.Setup(r => r.GetByIdAsync(memory.Id, It.IsAny<CancellationToken>())).ReturnsAsync(memory);
        _memoryRepository.Setup(r => r.ListEvidenceAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(memory.Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEvidence>)[evidence]);
        _memoryRepository.Setup(r => r.ListEntitiesAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(memory.Id)), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEntity>)[entity]);

        var own = await _sut.GetMemoryAsync(projectId, memory.Id, CancellationToken.None);
        var foreign = await _sut.GetMemoryAsync(Guid.NewGuid(), memory.Id, CancellationToken.None);

        own.Should().NotBeNull();
        own!.Memory.Should().BeSameAs(memory);
        own.Evidence.Should().ContainSingle().Which.Should().BeSameAs(evidence);
        own.Entities.Should().ContainSingle().Which.Should().BeSameAs(entity);
        foreign.Should().BeNull();
    }

    [Fact]
    public async Task ForgetMemoryAsync_hard_deletes_only_a_memory_of_the_owning_project()
    {
        var projectId = Guid.NewGuid();
        var memory = MakeMemory(projectId, MemoryType.Decision, "JWT", "Short-lived tokens");
        _memoryRepository.Setup(r => r.GetByIdAsync(memory.Id, It.IsAny<CancellationToken>())).ReturnsAsync(memory);
        _memoryStore.Setup(s => s.InTransactionAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task<bool>> work, CancellationToken _) => work());
        _memoryStore.Setup(s => s.ListEvidenceAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _memoryStore.Setup(s => s.DeleteAsync(memory.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var foreign = await _sut.ForgetMemoryAsync(Guid.NewGuid(), memory.Id, CancellationToken.None);
        _memoryStore.Verify(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        var own = await _sut.ForgetMemoryAsync(projectId, memory.Id, CancellationToken.None);
        var missing = await _sut.ForgetMemoryAsync(projectId, Guid.NewGuid(), CancellationToken.None);

        foreign.Should().BeFalse();
        own.Should().BeTrue();
        missing.Should().BeFalse();
        _memoryStore.Verify(s => s.DeleteAsync(memory.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- 6. ProjectInfoAsync uses ISymbolRepository.CountByProjectAsync instead of an O(files) ListByFileAsync loop ----

    [Fact]
    public async Task ProjectInfoAsync_uses_CountByProjectAsync_for_symbol_count_and_reports_correct_stats()
    {
        var projectId = Guid.NewGuid();
        var project = new Project { Id = projectId, Name = "Demo", RootPath = "/repo", CreatedAt = DateTimeOffset.UtcNow };

        var olderUpdatedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var newerUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var files = new List<DomainFile>
        {
            new() { Id = Guid.NewGuid(), ProjectId = projectId, RelativePath = "a.cs", ContentHash = "h1", UpdatedAt = olderUpdatedAt },
            new() { Id = Guid.NewGuid(), ProjectId = projectId, RelativePath = "b.cs", ContentHash = "h2", UpdatedAt = newerUpdatedAt }
        };

        _projectRepository.Setup(r => r.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _fileRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainFile>)files);
        _symbolRepository.Setup(r => r.CountByProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        var result = await _sut.ProjectInfoAsync(projectId, CancellationToken.None);

        var info = result.Should().BeOfType<ProjectInfo>().Subject;
        info.SymbolsIndexed.Should().Be(42);
        info.FilesIndexed.Should().Be(2);
        info.LastIndexedAt.Should().Be(newerUpdatedAt);

        _symbolRepository.Verify(r => r.CountByProjectAsync(projectId, It.IsAny<CancellationToken>()), Times.Once);
        // The old N+1 loop called ListByFileAsync once per file.
        _symbolRepository.Verify(r => r.ListByFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProjectInfoAsync_reports_null_LastIndexedAt_when_project_has_no_files()
    {
        var projectId = Guid.NewGuid();
        var project = new Project { Id = projectId, Name = "Empty", RootPath = "/repo", CreatedAt = DateTimeOffset.UtcNow };

        _projectRepository.Setup(r => r.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _fileRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainFile>)[]);
        _symbolRepository.Setup(r => r.CountByProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var result = await _sut.ProjectInfoAsync(projectId, CancellationToken.None);

        var info = result.Should().BeOfType<ProjectInfo>().Subject;
        info.FilesIndexed.Should().Be(0);
        info.SymbolsIndexed.Should().Be(0);
        info.LastIndexedAt.Should().BeNull();
    }

    [Fact]
    public async Task ProjectInfoAsync_throws_when_project_does_not_exist()
    {
        var projectId = Guid.NewGuid();
        _projectRepository.Setup(r => r.GetByIdAsync(projectId, It.IsAny<CancellationToken>())).ReturnsAsync((Project?)null);

        var act = () => _sut.ProjectInfoAsync(projectId, CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ---- 7. RememberExplicitlyAsync: explicit-remember bypasses the event/session extraction
    // pipeline and hands one Explicit candidate straight to IMemoryConsolidator. ----

    [Fact]
    public async Task RememberExplicitlyAsync_hands_an_explicit_candidate_straight_to_the_consolidator()
    {
        var projectId = Guid.NewGuid();
        var consolidated = MakeMemory(projectId, MemoryType.Fact, "Title", "Body");

        _memoryConsolidator
            .Setup(c => c.ConsolidateAsync(projectId, null, It.IsAny<MemoryCandidate>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationResult(consolidated, ConsolidationOutcome.Created));

        var result = await _sut.RememberExplicitlyAsync(projectId, "Always use tabs, not spaces.", type: null, CancellationToken.None);

        result.Should().BeSameAs(consolidated);
        _memoryConsolidator.Verify(
            c => c.ConsolidateAsync(
                projectId,
                null,
                It.Is<MemoryCandidate>(cand =>
                    cand.Type == MemoryType.Fact &&
                    cand.Body == "Always use tabs, not spaces." &&
                    cand.Source == EvidenceSource.Explicit &&
                    cand.Facts.Count == 0 && cand.Concepts.Count == 0 &&
                    cand.FilesRead.Count == 0 && cand.FilesModified.Count == 0 &&
                    cand.Topic == null),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);

        _jobQueue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _sessionRepository.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _memoryRepository.Verify(r => r.ListByProjectAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null, MemoryType.Fact)]
    [InlineData(MemoryType.Decision, MemoryType.Decision)]
    public async Task RememberExplicitlyAsync_uses_the_given_type_or_defaults_to_Fact(MemoryType? requested, MemoryType expected)
    {
        var projectId = Guid.NewGuid();
        _memoryConsolidator
            .Setup(c => c.ConsolidateAsync(projectId, null, It.IsAny<MemoryCandidate>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid? _, MemoryCandidate cand, bool _, CancellationToken _) =>
                new ConsolidationResult(MakeMemory(projectId, cand.Type, cand.Title, cand.Body), ConsolidationOutcome.Created));

        var result = await _sut.RememberExplicitlyAsync(projectId, "Use SQLite for the cache.", requested, CancellationToken.None);

        result.Type.Should().Be(expected);
    }
}
