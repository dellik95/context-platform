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
using Microsoft.Extensions.Logging;
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
    private readonly Mock<ISessionSummaryVectorIndex> _sessionSummaryVectorIndex = new();
    private readonly Mock<IProviderResolver> _providerResolver = new();
    private readonly Mock<IJobRepository> _jobRepository = new();
    private readonly Mock<IEmbeddingGenerator<string, Embedding<float>>> _embeddingGenerator = new();
    private readonly Mock<ILogger<ContextEngine>> _logger = new();
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
            _sessionSummaryVectorIndex.Object,
            _providerResolver.Object,
            new PassthroughUnitOfWork(),
            _jobRepository.Object,
            _logger.Object);

        _jobRepository.Setup(r => r.EnqueueAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job job, CancellationToken _) => job);

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

        // Memory search always embeds the query; with no stored memory embeddings it ranks lexically
        // unless a test sets up similarities.
        _embeddingGenerator.Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(QueryVector)]));
        _providerResolver.Setup(p => p.GetEmbeddingGeneratorAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_embeddingGenerator.Object);
        _memoryStore.Setup(s => s.GetEmbeddingsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, MemoryVector>());
    }

    // The mocked generator reports no metadata, so its provider/model key is "unknown".
    private const string CurrentModelKey = "unknown";
    private static readonly float[] QueryVector = [1f, 0f];

    /// <summary>Stores an embedding per memory at exactly the given cosine similarity to <see cref="QueryVector"/>.</summary>
    private void SetupSimilarities(params (DomainMemory Memory, double Similarity)[] similarities) =>
        SetupEmbeddings([.. similarities.Select(s => (s.Memory, s.Similarity, CurrentModelKey))]);

    private void SetupEmbeddings(params (DomainMemory Memory, double Similarity, string ModelKey)[] embeddings) =>
        _memoryStore.Setup(s => s.GetEmbeddingsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, CancellationToken _) => embeddings
                .Where(e => ids.Contains(e.Memory.Id))
                .ToDictionary(
                    e => e.Memory.Id,
                    e => new MemoryVector(e.ModelKey, [(float)e.Similarity, (float)Math.Sqrt(1 - (e.Similarity * e.Similarity))])));

    private void VerifyWarnings(Times times) =>
        _logger.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

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

    private ContextEngine CreateSutWith(IUnitOfWork unitOfWork) => new(
        new ContextPackBuilder(_searchEngine.Object), _jobQueue.Object, _graphTraversal.Object, _graphCacheProvider.Object,
        _projectRepository.Object, _fileRepository.Object, _symbolRepository.Object, _chunkRepository.Object, _memoryRepository.Object,
        _sessionRepository.Object, _deliveryCache, _projectSettingsService.Object, _memoryConsolidator.Object, _memoryStore.Object,
        _sessionSummaryVectorIndex.Object, _providerResolver.Object, unitOfWork, _jobRepository.Object, _logger.Object);

    private sealed class PassthroughUnitOfWork : IUnitOfWork
    {
        public bool InTransaction => false;

        public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct) => work();
    }

    private sealed class OpenTransactionUnitOfWork : IUnitOfWork
    {
        public bool InTransaction => true;

        public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct) => work();
    }

    private sealed class RollbackTrackingUnitOfWork : IUnitOfWork
    {
        public bool InTransaction => false;

        public bool Committed { get; private set; }

        public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
        {
            var result = await work();
            Committed = true;
            return result;
        }
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

    // ---- 1b. Query-aware memory sections ----

    private void SetupLiveMemories(Guid projectId, params DomainMemory[] memories) =>
        _memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(memories);

    [Fact]
    public async Task SearchContextAsync_answers_a_domain_question_with_the_matching_memory_only()
    {
        var projectId = Guid.NewGuid();
        var canonical = MakeMemory(projectId, MemoryType.Decision, "Token refresh strategy",
            "Refresh tokens rotate on every refresh and expire after 14 days", confidence: 0.8);
        canonical.SourceCount = 6;
        canonical.Topic = "Auth";
        var unrelated = MakeMemory(projectId, MemoryType.Architecture, "Dashboard layout", "Panels are lazy-loaded Angular routes", confidence: 0.9);
        SetupLiveMemories(projectId, canonical, unrelated);

        var pack = await _sut.SearchContextAsync(
            new SearchQuery(projectId, "how does token refresh work", null, TokenBudget: 5000), CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle()
            .Which.Should().Be("[Decision · Auth · conf 0.80 · 6 sources] Token refresh strategy: Refresh tokens rotate on every refresh and expire after 14 days");
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
    public async Task SearchContextAsync_matches_memories_through_entities_and_semantic_similarity()
    {
        var projectId = Guid.NewGuid();
        var byEntity = MakeMemory(projectId, MemoryType.Decision, "Retry budget", "Three attempts then dead-letter");
        var bySemantics = MakeMemory(projectId, MemoryType.Behavior, "Backoff", "Exponential with jitter");
        var unrelated = MakeMemory(projectId, MemoryType.Fact, "Dashboard theme", "Dark by default");
        SetupLiveMemories(projectId, byEntity, bySemantics, unrelated);

        _memoryRepository.Setup(r => r.ListLiveEntitiesByKeysAsync(projectId, It.Is<IReadOnlyCollection<string>>(k => k.Contains("ticket:phun-123")), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEntity>)[new MemoryEntity { ProjectId = projectId, MemoryId = byEntity.Id, Key = "ticket:phun-123", Display = "PHUN-123" }]);
        SetupSimilarities((byEntity, 0.2), (bySemantics, 0.7), (unrelated, 0.3));

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
        var sessionKey = "claude-session-1";
        var nodeId = Guid.NewGuid();

        var hit = new SearchHit(nodeId, "Symbol", "Foo.Bar", 1.0, "original snippet content");
        _searchEngine.Setup(s => s.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SearchHit>)[hit]);

        var query = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionKey: sessionKey);

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

        var firstQuery = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionKey: Guid.NewGuid().ToString());
        var secondQuery = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionKey: Guid.NewGuid().ToString());

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

        var query = new SearchQuery(projectId, "foo", null, TokenBudget: 5000, SessionKey: null);

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

        await _sut.EndSessionAsync(session.ProjectId, sessionId, CancellationToken.None);

        _jobRepository.Verify(
            r => r.EnqueueAsync(
                It.Is<Job>(j => j.Type == JobType.SummarizeSession && j.ProjectId == session.ProjectId
                    && System.Text.Json.JsonSerializer.Deserialize<SummarizeSessionPayload>(j.PayloadJson, (System.Text.Json.JsonSerializerOptions?)null)!.SessionId == sessionId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _jobQueue.Verify(q => q.WakeAsync(session.ProjectId, It.IsAny<CancellationToken>()), Times.Once);

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
        _jobRepository.Verify(r => r.EnqueueAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobQueue.Verify(q => q.WakeAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Jobs_inserted_inside_a_callers_open_transaction_are_not_woken_until_that_caller_commits()
    {
        var session = new Session { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow.AddHours(-1), Status = SessionStatus.Active };
        _sessionRepository.Setup(r => r.GetByIdAsync(session.ProjectId, session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _sessionRepository.Setup(r => r.ClaimEventsForExtractionAsync(session.Id, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var sut = CreateSutWith(new OpenTransactionUnitOfWork());

        await sut.EndSessionAsync(session.ProjectId, session.Id, CancellationToken.None);
        await sut.RecordEventAsync(session.ProjectId, session.Id, SessionEventType.ToolCall, new { tool = "Edit" }, CancellationToken.None);
        await sut.ExtractMemoryFromCompactSummaryAsync(session.ProjectId, session.Id, "Switched auth to PKCE.", CancellationToken.None);
        await sut.RequestSessionSummaryAsync(session.ProjectId, session.Id, CancellationToken.None);

        _jobRepository.Verify(r => r.EnqueueAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()), Times.AtLeast(4));
        _jobQueue.Verify(q => q.WakeAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSessionAsync_fills_in_the_working_directory_of_a_reused_session_that_has_none()
    {
        var projectId = Guid.NewGuid();
        var existing = new Session { Id = Guid.NewGuid(), ProjectId = projectId, ClaudeSessionId = "claude-1", StartedAt = DateTimeOffset.UtcNow, Status = SessionStatus.Active, Cwd = null };
        _sessionRepository.Setup(r => r.GetByClaudeSessionIdAsync(projectId, "claude-1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var id = await _sut.StartSessionAsync(projectId, "claude-1", CancellationToken.None, cwd: "/repo");

        id.Should().Be(existing.Id);
        existing.Cwd.Should().Be("/repo");
        _sessionRepository.Verify(r => r.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);

        await _sut.StartSessionAsync(projectId, "claude-1", CancellationToken.None, cwd: "/elsewhere");

        existing.Cwd.Should().Be("/repo", "a recorded working directory is never replaced");
        _sessionRepository.Verify(r => r.UpdateAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
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
        _jobRepository.Verify(
            r => r.EnqueueAsync(
                It.Is<Job>(j => j.Type == JobType.ExtractMemory && j.ProjectId == projectId && j.Priority == JobPriority.Session
                    && System.Text.Json.JsonSerializer.Deserialize<ExtractMemoryPayload>(j.PayloadJson, (System.Text.Json.JsonSerializerOptions?)null)!.EventBatchId == batchId
                    && System.Text.Json.JsonSerializer.Deserialize<ExtractMemoryPayload>(j.PayloadJson, (System.Text.Json.JsonSerializerOptions?)null)!.SessionId == sessionId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _jobQueue.Verify(q => q.WakeAsync(projectId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordEventAsync_keeps_the_supplied_event_id_and_timestamp()
    {
        var sessionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var receivedAt = DateTimeOffset.UtcNow.AddMinutes(-3);

        await _sut.RecordEventAsync(Guid.NewGuid(), sessionId, SessionEventType.Prompt, new { prompt = "hi" }, CancellationToken.None, eventId, receivedAt);

        _sessionRepository.Verify(
            r => r.AddEventAsync(It.Is<SessionEvent>(e => e.Id == eventId && e.Timestamp == receivedAt && e.SessionId == sessionId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordEventAsync_does_not_commit_the_event_when_queueing_the_batch_job_fails()
    {
        var sessionId = Guid.NewGuid();
        var unitOfWork = new RollbackTrackingUnitOfWork();
        var sut = CreateSutWith(unitOfWork);
        _sessionRepository.Setup(r => r.ClaimEventsForExtractionAsync(sessionId, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        _jobRepository.Setup(r => r.EnqueueAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database is locked"));

        var act = () => sut.RecordEventAsync(Guid.NewGuid(), sessionId, SessionEventType.ToolCall, new { tool = "Edit" }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        unitOfWork.Committed.Should().BeFalse("the event, the batch claim and the job must succeed or fail together");
        _jobQueue.Verify(q => q.WakeAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _jobRepository.Verify(r => r.EnqueueAsync(It.Is<Job>(j => j.Type == JobType.ExtractMemory && j.ProjectId == session.ProjectId), It.IsAny<CancellationToken>()), Times.Once);
        _jobRepository.Verify(r => r.EnqueueAsync(It.Is<Job>(j => j.Type == JobType.SummarizeSession && j.ProjectId == session.ProjectId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExtractMemoryFromCompactSummaryAsync_enqueues_the_summary_and_ignores_blank_input()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await _sut.ExtractMemoryFromCompactSummaryAsync(projectId, sessionId, "   ", CancellationToken.None);
        await _sut.ExtractMemoryFromCompactSummaryAsync(projectId, sessionId, "Switched auth to PKCE.", CancellationToken.None);

        _jobRepository.Verify(
            r => r.EnqueueAsync(
                It.Is<Job>(j => j.Type == JobType.ExtractMemory && j.ProjectId == projectId && j.Priority == JobPriority.Session
                    && System.Text.Json.JsonSerializer.Deserialize<ExtractMemoryPayload>(j.PayloadJson, (System.Text.Json.JsonSerializerOptions?)null)!.CompactSummary == "Switched auth to PKCE."),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _jobQueue.Verify(q => q.WakeAsync(projectId, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task SearchMemoryAsync_filters_by_topic()
    {
        var projectId = Guid.NewGuid();
        var authDecision = MakeMemory(projectId, MemoryType.Decision, "JWT", "Short-lived tokens");
        authDecision.Topic = "Auth";
        var authConstraint = MakeMemory(projectId, MemoryType.Constraint, "Signing keys", "JWT signing keys rotate monthly");
        authConstraint.Topic = "Auth";
        var dbFact = MakeMemory(projectId, MemoryType.Fact, "SQLite", "WAL mode");
        dbFact.Topic = "Storage";
        SetupLiveMemories(projectId, authDecision, authConstraint, dbFact);

        var auth = await _sut.SearchMemoryAsync(projectId, string.Empty, includeSuperseded: false, CancellationToken.None, topic: " auth ");

        auth.Should().BeEquivalentTo([authDecision, authConstraint]);
    }

    [Fact]
    public async Task SearchMemoryAsync_falls_back_to_lexical_matching_and_warns_when_embedding_fails()
    {
        var projectId = Guid.NewGuid();
        var match = MakeMemory(projectId, MemoryType.Fact, "Migrations", "EF Core migrations run on startup");
        var partial = MakeMemory(projectId, MemoryType.Fact, "Startup", "The host warms caches on startup");
        var other = MakeMemory(projectId, MemoryType.Fact, "Dashboard theme", "Dark by default");
        SetupLiveMemories(projectId, partial, match, other);
        SetupSimilarities((match, 0.9), (partial, 0.9), (other, 0.9));
        _providerResolver.Setup(p => p.GetEmbeddingGeneratorAsync(projectId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        var result = await _sut.SearchMemoryAsync(projectId, "migrations on startup", includeSuperseded: false, CancellationToken.None);

        result.Should().Equal(match, partial);
        VerifyWarnings(Times.Once());
    }

    [Fact]
    public async Task SearchMemoryAsync_ranks_by_semantic_similarity_above_relevance_and_type()
    {
        var projectId = Guid.NewGuid();
        var answer = MakeMemory(projectId, MemoryType.Fact, "Log scrubbing", "SecretRedactingEmbeddingGeneratorDecorator strips tokens before logging",
            confidence: 0.3, lastReinforcedAt: DateTimeOffset.UtcNow.AddDays(-120));
        var related = MakeMemory(projectId, MemoryType.Decision, "Provider keys", "API keys live in user secrets", confidence: 0.95, pinned: true);
        var unrelated = MakeMemory(projectId, MemoryType.Architecture, "Dashboard layout", "Panels are lazy-loaded routes", confidence: 0.95, pinned: true);
        SetupLiveMemories(projectId, related, unrelated, answer);
        SetupSimilarities((answer, 0.78), (related, 0.61), (unrelated, 0.32));

        var result = await _sut.SearchMemoryAsync(projectId, "how do we keep credentials out of the logs", includeSuperseded: false, CancellationToken.None);

        result.Should().Equal(answer, related);
        VerifyWarnings(Times.Never());
    }

    [Fact]
    public async Task SearchMemoryAsync_admits_a_semantically_weak_memory_only_on_a_strong_lexical_or_entity_match()
    {
        var projectId = Guid.NewGuid();
        var semantic = MakeMemory(projectId, MemoryType.Behavior, "Delivery attempts", "Failed deliveries back off exponentially");
        var everyTerm = MakeMemory(projectId, MemoryType.Fact, "Webhook retry budget", "Configured per tenant under PAY-231");
        var someTerms = MakeMemory(projectId, MemoryType.Fact, "Retry button", "The dashboard can retry a failed job");
        var byEntity = MakeMemory(projectId, MemoryType.Fact, "Ticket", "Tracked separately");
        SetupLiveMemories(projectId, someTerms, byEntity, everyTerm, semantic);
        SetupSimilarities((semantic, 0.72), (everyTerm, 0.35), (someTerms, 0.40), (byEntity, 0.10));
        _memoryRepository.Setup(r => r.ListLiveEntitiesByKeysAsync(projectId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<MemoryEntity>)[new MemoryEntity { ProjectId = projectId, MemoryId = byEntity.Id, Key = "ticket:pay-231", Display = "PAY-231" }]);

        var result = await _sut.SearchMemoryAsync(projectId, "webhook retry budget PAY-231", includeSuperseded: false, CancellationToken.None);

        result.Should().Equal(semantic, everyTerm, byEntity);
    }

    [Fact]
    public async Task SearchMemoryAsync_lets_lexical_matches_reorder_close_semantic_hits()
    {
        var projectId = Guid.NewGuid();
        var named = MakeMemory(projectId, MemoryType.Fact, "Stale job requeue", "JobDispatcher requeues Running jobs at startup");
        var closer = MakeMemory(projectId, MemoryType.Fact, "Hung jobs", "ForceRun cancels a hung job and reruns it");
        SetupLiveMemories(projectId, closer, named);
        SetupSimilarities((named, 0.62), (closer, 0.66));

        var result = await _sut.SearchMemoryAsync(projectId, "requeue stale job", includeSuperseded: false, CancellationToken.None);

        result.Should().Equal(named, closer);
    }

    [Fact]
    public async Task SearchMemoryAsync_ignores_embeddings_from_another_model()
    {
        var projectId = Guid.NewGuid();
        var current = MakeMemory(projectId, MemoryType.Fact, "Backoff", "Exponential with jitter");
        var stale = MakeMemory(projectId, MemoryType.Fact, "Old model", "Embedded before the model changed");
        SetupLiveMemories(projectId, current, stale);
        SetupEmbeddings((current, 0.7, CurrentModelKey), (stale, 0.99, "ollama:old-model"));

        var result = await _sut.SearchMemoryAsync(projectId, "how are retries spaced out", includeSuperseded: false, CancellationToken.None);

        result.Should().Equal(current);
    }

    [Fact]
    public async Task SearchContextAsync_ranks_historical_notes_like_memory_search()
    {
        var projectId = Guid.NewGuid();
        var answer = MakeMemory(projectId, MemoryType.Fact, "Log scrubbing", "Tokens are stripped before logging", confidence: 0.3);
        var related = MakeMemory(projectId, MemoryType.Decision, "Provider keys", "API keys live in user secrets", confidence: 0.95, pinned: true);
        var unrelated = MakeMemory(projectId, MemoryType.Architecture, "Dashboard layout", "Panels are lazy-loaded routes", confidence: 0.95, pinned: true);
        SetupLiveMemories(projectId, related, unrelated, answer);
        SetupSimilarities((answer, 0.78), (related, 0.61), (unrelated, 0.32));

        var pack = await _sut.SearchContextAsync(
            new SearchQuery(projectId, "how do we keep credentials out of the logs", null, TokenBudget: 5000), CancellationToken.None);

        pack.HistoricalNotes.Should().HaveCount(2);
        pack.HistoricalNotes[0].Should().Contain(answer.Title);
        pack.HistoricalNotes[1].Should().Contain(related.Title);
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

    // ---- 8. RequestRememberAsync / RequestForgetAsync: durable queueing instead of inline work ----

    [Fact]
    public async Task RequestRememberAsync_queues_a_RememberMemory_job_without_consolidating_inline()
    {
        var projectId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        _jobQueue
            .Setup(q => q.EnqueueAsync(JobType.RememberMemory, It.IsAny<object>(), projectId, JobPriority.Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var result = await _sut.RequestRememberAsync(
            projectId, "Use SQLite for the cache.", MemoryType.Decision, CancellationToken.None,
            title: "Cache", topic: "Caching", facts: ["SQLite"], filesModified: ["src/Cache.cs"]);

        result.Should().Be(jobId);
        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.RememberMemory,
                It.Is<object>(p => p is RememberMemoryPayload
                    && ((RememberMemoryPayload)p).Content == "Use SQLite for the cache."
                    && ((RememberMemoryPayload)p).Type == MemoryType.Decision
                    && ((RememberMemoryPayload)p).Title == "Cache"
                    && ((RememberMemoryPayload)p).Topic == "Caching"
                    && ((RememberMemoryPayload)p).Facts!.SequenceEqual(new[] { "SQLite" })
                    && ((RememberMemoryPayload)p).FilesModified!.SequenceEqual(new[] { "src/Cache.cs" })),
                projectId, JobPriority.Session, It.IsAny<CancellationToken>()),
            Times.Once);
        _memoryConsolidator.Verify(
            c => c.ConsolidateAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<MemoryCandidate>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestRememberAsync_rejects_blank_content_without_queueing(string content)
    {
        var act = () => _sut.RequestRememberAsync(Guid.NewGuid(), content, type: null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        _jobQueue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestForgetAsync_queues_a_ForgetMemory_job_only_for_a_memory_of_the_owning_project()
    {
        var projectId = Guid.NewGuid();
        var memory = MakeMemory(projectId, MemoryType.Decision, "JWT", "Short-lived tokens");
        var jobId = Guid.NewGuid();
        _memoryRepository.Setup(r => r.GetByIdAsync(memory.Id, It.IsAny<CancellationToken>())).ReturnsAsync(memory);
        _jobQueue
            .Setup(q => q.EnqueueAsync(JobType.ForgetMemory, It.IsAny<object>(), projectId, JobPriority.Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobId);

        var foreign = await _sut.RequestForgetAsync(Guid.NewGuid(), memory.Id, CancellationToken.None);
        var missing = await _sut.RequestForgetAsync(projectId, Guid.NewGuid(), CancellationToken.None);
        var own = await _sut.RequestForgetAsync(projectId, memory.Id, CancellationToken.None);

        foreign.Should().BeNull();
        missing.Should().BeNull();
        own.Should().Be(jobId);
        _jobQueue.Verify(
            q => q.EnqueueAsync(JobType.ForgetMemory, It.Is<object>(p => p is ForgetMemoryPayload && ((ForgetMemoryPayload)p).MemoryId == memory.Id),
                projectId, JobPriority.Session, It.IsAny<CancellationToken>()),
            Times.Once);
        _memoryStore.Verify(s => s.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
