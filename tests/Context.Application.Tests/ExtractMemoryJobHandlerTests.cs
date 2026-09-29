using System.Text.Json;
using Context.Application.Adapters;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Infrastructure.Repositories;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Extraction;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;

namespace Context.Application.Tests;

/// <summary>
/// The first test runs the real extractor, consolidator and SQLite-backed store: a retried job
/// re-proposes the same candidate, which must reinforce the memory the first pass created. The other
/// tests only check which source the handler reads.
/// </summary>
public sealed class ExtractMemoryJobHandlerTests : IDisposable
{
    private static readonly float[] FixedEmbedding = [1f, 0f];

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-extract-tests-{Guid.NewGuid():N}.db");

    private DbContextOptions<ContextDbContext> Options =>
        new DbContextOptionsBuilder<ContextDbContext>().UseSqlite($"Data Source={_dbPath}").Options;

    [Fact]
    public async Task HandleAsync_reprocessing_the_same_compact_summary_reinforces_instead_of_duplicating_an_identical_memory()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var payload = new ExtractMemoryPayload(sessionId, CompactSummary: "Decided to use SQLite for local caching.");

        await using var db = new ContextDbContext(Options);
        await db.Database.MigrateAsync();
        db.Projects.Add(new Project { Id = projectId, Name = "Payments", RootPath = "/repo/payments", CreatedAt = DateTimeOffset.UtcNow });
        db.Sessions.Add(new Session { Id = sessionId, ProjectId = projectId, StartedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using var vectorIndex = new SqliteMemoryVectorIndex(_dbPath, new FakeDbContextFactory(Options));
        var memories = new MemoryRepository(db);
        var store = new MemoryStoreAdapter(memories, db, vectorIndex);
        var codeVerifier = new CodeVerifier(new FileRepository(db), new SymbolRepository(db), new ProjectRepository(db));
        var resolver = new FakeProviderResolver(CandidateResponse());
        var usage = new FakeUsageRecorder();
        var extractor = new LlmMemoryExtractor(new MemoryConsolidator(store, codeVerifier, resolver, usage), resolver, usage);
        var sut = new ExtractMemoryJobHandler(extractor, new Mock<ISessionRepository>().Object);

        await sut.HandleAsync(MakeJob(projectId, payload), CancellationToken.None);
        await sut.HandleAsync(MakeJob(projectId, payload), CancellationToken.None);

        var persisted = await db.Memories.AsNoTracking().Where(m => m.ProjectId == projectId).ToListAsync();
        var memory = persisted.Should().ContainSingle().Subject;
        memory.Type.Should().Be(MemoryType.Decision);
        memory.Status.Should().Be(MemoryStatus.Active);
        memory.Body.Should().NotContain("CONFIDENCE");
        memory.ReinforcementCount.Should().Be(1);
        memory.SourceCount.Should().Be(1, "both passes come from the same session");
        memory.Tier.Should().Be(MemoryTier.Working);

        var evidence = await db.MemoryEvidence.AsNoTracking().Where(e => e.MemoryId == memory.Id).ToListAsync();
        evidence.Select(e => e.Relation).Should().BeEquivalentTo([EvidenceRelation.Created, EvidenceRelation.Reinforced]);
        evidence.Should().OnlyContain(e => e.Source == EvidenceSource.CompactSummary && e.SessionId == sessionId);
        (await db.MemoryEmbeddings.AsNoTracking().CountAsync(e => e.MemoryId == memory.Id)).Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_extracts_from_the_claimed_event_batch_and_marks_it_done()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        IReadOnlyList<SessionEvent> events =
        [
            new() { Id = Guid.NewGuid(), SessionId = sessionId, Type = SessionEventType.ToolCall, PayloadJson = "{}", Timestamp = DateTimeOffset.UtcNow },
        ];

        var sessionRepository = new Mock<ISessionRepository>();
        sessionRepository.Setup(r => r.GetEventsByExtractionBatchAsync(projectId, sessionId, batchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);
        var extractor = new Mock<IMemoryExtractor>();
        extractor.Setup(e => e.ExtractFromEventsAsync(projectId, sessionId, events, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = new ExtractMemoryJobHandler(extractor.Object, sessionRepository.Object);

        await sut.HandleAsync(MakeJob(projectId, new ExtractMemoryPayload(sessionId, EventBatchId: batchId)), CancellationToken.None);

        extractor.Verify(e => e.ExtractFromEventsAsync(projectId, sessionId, events, It.IsAny<CancellationToken>()), Times.Once);
        sessionRepository.Verify(r => r.MarkExtractionBatchDoneAsync(batchId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_skips_an_event_batch_with_nothing_left_in_it()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var batchId = Guid.NewGuid();

        var sessionRepository = new Mock<ISessionRepository>();
        sessionRepository.Setup(r => r.GetEventsByExtractionBatchAsync(projectId, sessionId, batchId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var extractor = new Mock<IMemoryExtractor>();
        var sut = new ExtractMemoryJobHandler(extractor.Object, sessionRepository.Object);

        await sut.HandleAsync(MakeJob(projectId, new ExtractMemoryPayload(sessionId, EventBatchId: batchId)), CancellationToken.None);

        extractor.VerifyNoOtherCalls();
        sessionRepository.Verify(r => r.MarkExtractionBatchDoneAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_extracts_from_a_compact_summary_observed_when_the_job_was_queued()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const string summary = "Moved retry policy into ResilienceOptions.";
        var job = MakeJob(projectId, new ExtractMemoryPayload(sessionId, CompactSummary: summary));
        job.CreatedAt = DateTimeOffset.UtcNow.AddHours(-3);

        var extractor = new Mock<IMemoryExtractor>();
        extractor.Setup(e => e.ExtractFromCompactSummaryAsync(projectId, sessionId, summary, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = new ExtractMemoryJobHandler(extractor.Object, new Mock<ISessionRepository>().Object);

        await sut.HandleAsync(job, CancellationToken.None);

        extractor.Verify(e => e.ExtractFromCompactSummaryAsync(projectId, sessionId, summary, job.CreatedAt, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Job MakeJob(Guid projectId, ExtractMemoryPayload payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.ExtractMemory,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(payload),
        CreatedAt = DateTimeOffset.UtcNow
    };

    // CONFIDENCE is not part of the format any more; an old-style line must be swallowed, not leak into BODY.
    private static string CandidateResponse() =>
        """
        TYPE: Decision
        TITLE: Use SQLite for local caching
        BODY: The team decided to use SQLite for local caching due to zero-ops deployment.
        CONFIDENCE: 0.9
        END_MEMORY
        """;

    /// <summary>Always resolves to the one fixed chat client / embedding generator it was
    /// constructed with, regardless of projectId.</summary>
    private sealed class FakeProviderResolver(string candidateResponse) : IProviderResolver
    {
        private readonly IChatClient _chatClient = new FakeChatClient(candidateResponse);
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = new FakeEmbeddingGenerator(FixedEmbedding);

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(_embeddingGenerator);

        public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) => Task.FromResult(_chatClient);
    }

    /// <summary>Answers every extraction request with the same candidate. The second pass never asks
    /// the relation judge: an identical embedding of the same type takes the duplicate fast path.</summary>
    private sealed class FakeChatClient(string candidateResponse) : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("fake", null, "fake-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, candidateResponse)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeEmbeddingGenerator(float[] vector) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(vector))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // Fully qualified: Context.Infrastructure.Abstractions declares an IUsageRecorder too.
    private sealed class FakeUsageRecorder : Context.Memory.Abstractions.IUsageRecorder
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDbContextFactory(DbContextOptions<ContextDbContext> options) : IDbContextFactory<ContextDbContext>
    {
        public ContextDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
