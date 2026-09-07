using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Application.Settings;
using FluentAssertions;
using Moq;
using DomainFile = Context.Domain.Entities.File;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

/// <summary>
/// Tests for <see cref="ContextEngine"/> (CONTRACTS.md §10 facade). <see cref="ContextPackBuilder"/>
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
    private readonly Mock<IMemoryStore> _memoryStore = new();
    private readonly Mock<IMemoryConsolidator> _memoryConsolidator = new();
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
            _memoryStore.Object,
            _memoryConsolidator.Object);

        // Neutral defaults so tests only need to configure what they actually exercise.
        _symbolRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Symbol?)null);
        _fileRepository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainFile?)null);
        _memoryRepository.Setup(r => r.ListByProjectAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[]);
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
            SupersededById = supersededById
        };
    }

    // ---- 1. SearchContextAsync merges ranked DomainMemory records into historical notes / open issues ----

    [Fact]
    public async Task SearchContextAsync_merges_ranked_memories_into_historical_notes_and_open_issues()
    {
        var projectId = Guid.NewGuid();

        var decision = MakeMemory(projectId, MemoryType.Decision, "Use SQLite", "Because it is simple", confidence: 0.9, pinned: true);
        var issue = MakeMemory(projectId, MemoryType.Issue, "Flaky test", "CI intermittently fails", confidence: 0.8);

        _memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[decision, issue]);

        var query = new SearchQuery(projectId, "sqlite", null, TokenBudget: 5000);

        var pack = await _sut.SearchContextAsync(query, CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle()
            .Which.Should().Be("[Decision] Use SQLite: Because it is simple");
        pack.OpenIssues.Should().ContainSingle()
            .Which.Should().Be("[Issue] Flaky test: CI intermittently fails");
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

        var query = new SearchQuery(projectId, "status", null, TokenBudget: 5000);

        var pack = await _sut.SearchContextAsync(query, CancellationToken.None);

        pack.HistoricalNotes.Should().ContainSingle();
        pack.OpenIssues.Should().ContainSingle();
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

    // ---- 5. SearchMemoryAsync: superseded exclusion (delegated to repository) + relevance ordering ----

    [Fact]
    public async Task SearchMemoryAsync_returns_exactly_what_the_repository_yields_for_the_requested_includeSuperseded_flag()
    {
        var projectId = Guid.NewGuid();
        var active = MakeMemory(projectId, MemoryType.Decision, "Active", "still valid");
        var superseded = MakeMemory(projectId, MemoryType.Decision, "Old", "replaced", supersededById: Guid.NewGuid());

        // IMemoryRepository.SearchAsync owns the actual superseded filtering (see its own doc
        // comment); ContextEngine.SearchMemoryAsync is a thin pass-through plus ranking, so this
        // test asserts it forwards the flag faithfully and does not itself re-introduce excluded rows.
        _memoryRepository.Setup(r => r.SearchAsync(projectId, "q", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[active]);
        _memoryRepository.Setup(r => r.SearchAsync(projectId, "q", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[active, superseded]);

        var excludingSuperseded = await _sut.SearchMemoryAsync(projectId, "q", includeSuperseded: false, CancellationToken.None);
        var includingSuperseded = await _sut.SearchMemoryAsync(projectId, "q", includeSuperseded: true, CancellationToken.None);

        excludingSuperseded.Should().ContainSingle().Which.Should().BeSameAs(active);
        includingSuperseded.Should().Contain([active, superseded]);

        _memoryRepository.Verify(r => r.SearchAsync(projectId, "q", false, It.IsAny<CancellationToken>()), Times.Once);
        _memoryRepository.Verify(r => r.SearchAsync(projectId, "q", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchMemoryAsync_ranks_a_pinned_memory_above_a_non_pinned_one_with_equal_confidence_and_age()
    {
        var projectId = Guid.NewGuid();
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-200);

        // Pinned memories skip the age-based decay entirely (DecayCalculator.ComputeDecayFactor
        // returns 1.0 for pinned regardless of age), so with equal confidence and equal (old)
        // LastReinforcedAt, the pinned one must outrank the non-pinned one whose decay has kicked in.
        var pinned = MakeMemory(projectId, MemoryType.Decision, "Pinned", "b", confidence: 0.5, pinned: true, lastReinforcedAt: oldTimestamp);
        var notPinned = MakeMemory(projectId, MemoryType.Decision, "NotPinned", "b", confidence: 0.5, pinned: false, lastReinforcedAt: oldTimestamp);

        _memoryRepository.Setup(r => r.SearchAsync(projectId, "q", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[notPinned, pinned]);

        var result = await _sut.SearchMemoryAsync(projectId, "q", includeSuperseded: false, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Should().BeSameAs(pinned);
        result[1].Should().BeSameAs(notPinned);
    }

    [Fact]
    public async Task SearchMemoryAsync_ranks_higher_confidence_above_lower_confidence_when_otherwise_equal()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var highConfidence = MakeMemory(projectId, MemoryType.Decision, "High", "b", confidence: 0.9, lastReinforcedAt: now);
        var lowConfidence = MakeMemory(projectId, MemoryType.Decision, "Low", "b", confidence: 0.2, lastReinforcedAt: now);

        _memoryRepository.Setup(r => r.SearchAsync(projectId, "q", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DomainMemory>)[lowConfidence, highConfidence]);

        var result = await _sut.SearchMemoryAsync(projectId, "q", includeSuperseded: false, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Should().BeSameAs(highConfidence);
        result[1].Should().BeSameAs(lowConfidence);
    }

    // ---- 6. ProjectInfoAsync uses ISymbolRepository.CountByProjectAsync (BACKLOG-06) instead of an O(files) ListByFileAsync loop ----

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
        // The N+1 loop this replaces (BACKLOG-06) called ListByFileAsync once per file -- assert it
        // is never called at all from ProjectInfoAsync any more.
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

    // ---- 7. RememberExplicitlyAsync (MEMORY_PIPELINE_PLAN.md §F/§H test #10): explicit-remember
    // bypasses the N-event/session-end extraction pipeline entirely - it builds one MemoryCandidate
    // from the raw content and hands it straight to IMemoryConsolidator, with zero dependency on
    // event counts, chunk state, or IJobQueue/ISessionRepository/IMemoryRepository. ----

    [Fact]
    public async Task RememberExplicitlyAsync_calls_the_consolidator_directly_with_zero_dependency_on_event_count_or_chunk_state()
    {
        var projectId = Guid.NewGuid();
        var existingWithEmbeddings = new List<ExistingMemoryWithEmbedding>();
        var consolidated = MakeMemory(projectId, MemoryType.Instruction, "Title", "Body");

        _memoryStore.Setup(s => s.GetExistingWithEmbeddingsAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ExistingMemoryWithEmbedding>)existingWithEmbeddings);
        _memoryConsolidator
            .Setup(c => c.ConsolidateAsync(projectId, null, It.IsAny<MemoryCandidate>(), existingWithEmbeddings, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationResult(consolidated, ConsolidationOutcome.Created));

        var result = await _sut.RememberExplicitlyAsync(projectId, "Always use tabs, not spaces.", type: null, CancellationToken.None);

        result.Should().BeSameAs(consolidated);
        _memoryConsolidator.Verify(
            c => c.ConsolidateAsync(
                projectId,
                null,
                It.Is<MemoryCandidate>(cand =>
                    cand.Type == MemoryType.Instruction &&
                    cand.Body == "Always use tabs, not spaces." &&
                    cand.Confidence == 0.95 &&
                    cand.Facts.Count == 0 && cand.Concepts.Count == 0 &&
                    cand.FilesRead.Count == 0 && cand.FilesModified.Count == 0 &&
                    cand.Topic == null),
                existingWithEmbeddings,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Bypasses the normal trigger/chunk pipeline entirely - no job enqueued, no session or
        // memory-repository read, unlike the periodic SummarizeSession -> ExtractMemory path.
        _jobQueue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _sessionRepository.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _memoryRepository.Verify(r => r.ListByProjectAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RememberExplicitlyAsync_defaults_to_MemoryType_Instruction_when_no_type_is_given()
    {
        var projectId = Guid.NewGuid();
        _memoryStore.Setup(s => s.GetExistingWithEmbeddingsAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ExistingMemoryWithEmbedding>)[]);
        _memoryConsolidator
            .Setup(c => c.ConsolidateAsync(projectId, null, It.IsAny<MemoryCandidate>(), It.IsAny<List<ExistingMemoryWithEmbedding>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid? _, MemoryCandidate cand, List<ExistingMemoryWithEmbedding> _, CancellationToken _) =>
                new ConsolidationResult(MakeMemory(projectId, cand.Type, cand.Title, cand.Body), ConsolidationOutcome.Created));

        var result = await _sut.RememberExplicitlyAsync(projectId, "Remember this.", type: null, CancellationToken.None);

        result.Type.Should().Be(MemoryType.Instruction);
    }

    [Fact]
    public async Task RememberExplicitlyAsync_honors_an_explicit_type_when_given()
    {
        var projectId = Guid.NewGuid();
        _memoryStore.Setup(s => s.GetExistingWithEmbeddingsAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ExistingMemoryWithEmbedding>)[]);
        _memoryConsolidator
            .Setup(c => c.ConsolidateAsync(projectId, null, It.IsAny<MemoryCandidate>(), It.IsAny<List<ExistingMemoryWithEmbedding>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid? _, MemoryCandidate cand, List<ExistingMemoryWithEmbedding> _, CancellationToken _) =>
                new ConsolidationResult(MakeMemory(projectId, cand.Type, cand.Title, cand.Body), ConsolidationOutcome.Created));

        var result = await _sut.RememberExplicitlyAsync(projectId, "Use SQLite for the cache.", MemoryType.Decision, CancellationToken.None);

        result.Type.Should().Be(MemoryType.Decision);
    }
}
