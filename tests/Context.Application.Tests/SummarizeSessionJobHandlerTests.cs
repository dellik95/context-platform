using System.Text.Json;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using FluentAssertions;
using Moq;

namespace Context.Application.Tests;

/// <summary>
/// Tests for <see cref="SummarizeSessionJobHandler"/>'s event-count gate (product feedback:
/// "I do not need to write empty sessions") — a truly empty session must not end up with a
/// persisted <see cref="SessionSummary"/> row. That bar only applies to
/// <see cref="Context.Application.Scheduling.SessionSummaryScheduler"/>'s periodic firing and a
/// session's first-ever chunk while it's still active: at session end
/// (<c>ContextEngine.EndSessionAsync</c>, which enqueues unconditionally) it's waived entirely, since
/// there's no later pass to catch a short session and its hook activity must still turn into a
/// summary and a chained ExtractMemory job.
/// </summary>
public sealed class SummarizeSessionJobHandlerTests
{
    private readonly Mock<ISessionSummarizer> _summarizer = new();
    private readonly Mock<ISessionRepository> _sessionRepository = new();
    private readonly Mock<IJobQueue> _jobQueue = new();
    private readonly Mock<IProjectSettingsService> _settingsService = new();
    private readonly SummarizeSessionJobHandler _sut;

    public SummarizeSessionJobHandlerTests()
    {
        _sut = new SummarizeSessionJobHandler(_summarizer.Object, _sessionRepository.Object, _jobQueue.Object, _settingsService.Object);
    }

    private static Job MakeJob(Guid projectId, Guid sessionId, bool isSessionEnd) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.SummarizeSession,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(new SummarizeSessionPayload(sessionId, isSessionEnd)),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static Session MakeSession(Guid projectId, Guid sessionId, DateTimeOffset startedAt) => new()
    {
        Id = sessionId,
        ProjectId = projectId,
        StartedAt = startedAt,
        Status = SessionStatus.Active
    };

    private static SessionEvent MakeEvent(DateTimeOffset timestamp) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        Type = SessionEventType.Prompt,
        PayloadJson = "{}",
        Timestamp = timestamp
    };

    private void SetUpSettings(int minEventsForSessionSummary = 15, int minEventsForMemoryExtraction = 20) =>
        _settingsService.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
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
                MinEventsForSessionSummary: minEventsForSessionSummary,
                MaxMemoryNotesPerSection: 20,
                MinEventsForMemoryExtraction: minEventsForMemoryExtraction));

    private void VerifyNothingWasPersisted()
    {
        _summarizer.Verify(s => s.SummarizeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
        _sessionRepository.Verify(r => r.AddSummaryChunkAsync(It.IsAny<SessionSummaryChunk>(), It.IsAny<CancellationToken>()), Times.Never);
        _sessionRepository.Verify(r => r.UpsertSummaryAsync(It.IsAny<SessionSummary>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobQueue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_writes_no_summary_for_a_session_with_zero_events_even_at_session_end()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var session = MakeSession(projectId, sessionId, DateTimeOffset.UtcNow.AddMinutes(-5));

        _sessionRepository.Setup(r => r.GetByIdAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _sessionRepository.Setup(r => r.GetSummaryChunksAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[]);
        _sessionRepository.Setup(r => r.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionEvent>)[]);
        SetUpSettings();

        await _sut.HandleAsync(MakeJob(projectId, sessionId, isSessionEnd: true), CancellationToken.None);

        VerifyNothingWasPersisted();
    }

    [Fact]
    public async Task HandleAsync_still_persists_and_extracts_when_a_short_session_ends_below_the_MinEventsForSessionSummary_bar()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var session = MakeSession(projectId, sessionId, startedAt);

        // Three trivial events - well under the default MinEventsForSessionSummary of 15 - and this
        // would be the session's first-ever chunk (no existing chunks). IsSessionEnd is true, so
        // there's no later pass to catch this session: the bar is waived and it must still get a
        // summary and a chained ExtractMemory job, or a short-lived session's hook activity would
        // never turn into memory at all.
        var events = new List<SessionEvent> { MakeEvent(startedAt.AddSeconds(1)), MakeEvent(startedAt.AddSeconds(2)), MakeEvent(startedAt.AddSeconds(3)) };

        var producedChunk = new SessionSummaryChunk
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Title = "Quick fix",
            EventCount = 3,
            FromEventAt = events[0].Timestamp,
            ToEventAt = events[^1].Timestamp,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        _sessionRepository.Setup(r => r.GetByIdAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _sessionRepository.SetupSequence(r => r.GetSummaryChunksAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[])
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[producedChunk]);
        _sessionRepository.Setup(r => r.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);
        _summarizer.Setup(s => s.SummarizeAsync(projectId, sessionId, startedAt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(producedChunk);
        _sessionRepository.Setup(r => r.AddSummaryChunkAsync(producedChunk, It.IsAny<CancellationToken>()))
            .ReturnsAsync(producedChunk);
        SetUpSettings(minEventsForSessionSummary: 15);

        await _sut.HandleAsync(MakeJob(projectId, sessionId, isSessionEnd: true), CancellationToken.None);

        _sessionRepository.Verify(r => r.AddSummaryChunkAsync(producedChunk, It.IsAny<CancellationToken>()), Times.Once);
        _sessionRepository.Verify(r => r.UpsertSummaryAsync(It.Is<SessionSummary>(sum => sum.Title == "Quick fix"), It.IsAny<CancellationToken>()), Times.Once);
        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.ExtractMemory,
                It.Is<object>(p => p is ExtractMemoryPayload && ((ExtractMemoryPayload)p).SummaryChunkId == producedChunk.Id),
                projectId,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_persists_a_summary_once_the_first_chunk_clears_the_MinEventsForSessionSummary_bar()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var session = MakeSession(projectId, sessionId, startedAt);
        var events = Enumerable.Range(1, 15).Select(i => MakeEvent(startedAt.AddSeconds(i))).ToList();

        var producedChunk = new SessionSummaryChunk
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Title = "Real work happened",
            EventCount = 15,
            FromEventAt = events[0].Timestamp,
            ToEventAt = events[^1].Timestamp,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        _sessionRepository.Setup(r => r.GetByIdAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _sessionRepository.SetupSequence(r => r.GetSummaryChunksAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[])
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[producedChunk]);
        _sessionRepository.Setup(r => r.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);
        _summarizer.Setup(s => s.SummarizeAsync(projectId, sessionId, startedAt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(producedChunk);
        _sessionRepository.Setup(r => r.AddSummaryChunkAsync(producedChunk, It.IsAny<CancellationToken>()))
            .ReturnsAsync(producedChunk);
        SetUpSettings(minEventsForSessionSummary: 15, minEventsForMemoryExtraction: 20);

        await _sut.HandleAsync(MakeJob(projectId, sessionId, isSessionEnd: false), CancellationToken.None);

        _sessionRepository.Verify(r => r.AddSummaryChunkAsync(producedChunk, It.IsAny<CancellationToken>()), Times.Once);
        _sessionRepository.Verify(r => r.UpsertSummaryAsync(It.Is<SessionSummary>(sum => sum.Title == "Real work happened"), It.IsAny<CancellationToken>()), Times.Once);
        // 15 events < MinEventsForMemoryExtraction(20) and this isn't a session-end pass, so
        // ExtractMemory should not be chained yet - unrelated to the summary gate, but worth
        // confirming the refactor didn't disturb this pre-existing rule.
        _jobQueue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_does_not_clobber_an_existing_summary_when_a_session_ends_with_no_new_events()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var session = MakeSession(projectId, sessionId, startedAt);

        // The session already has a real chunk from an earlier pass (e.g. SessionSummaryScheduler's
        // periodic firing) - nothing happened since then. Regression coverage for the bug where an
        // unconditional zero-event tail chunk at session end would still get merged in, and
        // SessionSummaryMerger.Merge keeps the LAST non-blank title - so a "No activity recorded"
        // tail chunk would silently overwrite the real title already recorded.
        var existingChunk = new SessionSummaryChunk
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Title = "Refactored the embedding pipeline",
            EventCount = 20,
            FromEventAt = startedAt,
            ToEventAt = startedAt.AddMinutes(30),
            GeneratedAt = startedAt.AddMinutes(30)
        };

        _sessionRepository.Setup(r => r.GetByIdAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _sessionRepository.Setup(r => r.GetSummaryChunksAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[existingChunk]);
        _sessionRepository.Setup(r => r.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionEvent>)[]); // nothing after existingChunk.ToEventAt either, since there's nothing at all
        SetUpSettings();

        await _sut.HandleAsync(MakeJob(projectId, sessionId, isSessionEnd: true), CancellationToken.None);

        VerifyNothingWasPersisted();
    }

    [Fact]
    public async Task HandleAsync_still_persists_a_small_trailing_chunk_when_the_session_already_has_a_real_summary()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var session = MakeSession(projectId, sessionId, startedAt);
        var existingChunk = new SessionSummaryChunk
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Title = "Refactored the embedding pipeline",
            EventCount = 20,
            FromEventAt = startedAt,
            ToEventAt = startedAt.AddMinutes(30),
            GeneratedAt = startedAt.AddMinutes(30)
        };

        // Only 2 new events since the last chunk - well under MinEventsForSessionSummary(15) - but
        // the session already has real content, so this is topping up an already-meaningful summary
        // (at session end), not manufacturing one from nothing. The threshold only gates a session's
        // very first chunk.
        var newEvents = new List<SessionEvent>
        {
            MakeEvent(existingChunk.ToEventAt.AddSeconds(1)),
            MakeEvent(existingChunk.ToEventAt.AddSeconds(2)),
        };

        var tailChunk = new SessionSummaryChunk
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Title = "Refactored the embedding pipeline",
            EventCount = 2,
            FromEventAt = newEvents[0].Timestamp,
            ToEventAt = newEvents[^1].Timestamp,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        _sessionRepository.Setup(r => r.GetByIdAsync(projectId, sessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        _sessionRepository.SetupSequence(r => r.GetSummaryChunksAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[existingChunk])
            .ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[existingChunk, tailChunk]);
        _sessionRepository.Setup(r => r.GetEventsAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newEvents);
        _summarizer.Setup(s => s.SummarizeAsync(projectId, sessionId, existingChunk.ToEventAt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tailChunk);
        _sessionRepository.Setup(r => r.AddSummaryChunkAsync(tailChunk, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tailChunk);
        SetUpSettings(minEventsForSessionSummary: 15, minEventsForMemoryExtraction: 20);

        await _sut.HandleAsync(MakeJob(projectId, sessionId, isSessionEnd: true), CancellationToken.None);

        _sessionRepository.Verify(r => r.AddSummaryChunkAsync(tailChunk, It.IsAny<CancellationToken>()), Times.Once);
        _sessionRepository.Verify(r => r.UpsertSummaryAsync(It.IsAny<SessionSummary>(), It.IsAny<CancellationToken>()), Times.Once);
        // IsSessionEnd bypasses the MinEventsForMemoryExtraction gate unconditionally (pre-existing rule).
        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.ExtractMemory,
                It.Is<object>(p => p is ExtractMemoryPayload && ((ExtractMemoryPayload)p).SummaryChunkId == tailChunk.Id),
                projectId,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
