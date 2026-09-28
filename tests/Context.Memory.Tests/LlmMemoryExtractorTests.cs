using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;

namespace Context.Memory.Tests;

/// <summary>
/// <see cref="LlmMemoryExtractor"/> with a mocked <see cref="IMemoryConsolidator"/>: what it hands to
/// consolidation and what it writes to the logger (counts and IDs only, never candidate text).
/// </summary>
public sealed class LlmMemoryExtractorTests
{
    private const string SecretTitle = "SECRET_TITLE_57f3d2 database migration notes";
    private const string SecretBody = "SECRET_BODY_57f3d2 rotated the prod credentials for the payments service";

    [Fact]
    public async Task ExtractFromEventsAsync_logs_counts_only_and_never_leaks_the_candidates_title_or_body_text()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var events = MakeEvents(sessionId);

        var consolidator = new Mock<IMemoryConsolidator>();
        consolidator
            .Setup(c => c.ConsolidateAsync(projectId, sessionId, It.IsAny<MemoryCandidate>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid? _, MemoryCandidate cand, bool _, CancellationToken _) =>
                new ConsolidationResult(MakeMemory(projectId, cand), ConsolidationOutcome.Created));

        var resolver = new FakeProviderResolver(new FakeChatClient(CandidateResponse()));
        var logger = new CapturingLogger<LlmMemoryExtractor>();
        var sut = new LlmMemoryExtractor(consolidator.Object, resolver, new FakeUsageRecorder(), logger);

        await sut.ExtractFromEventsAsync(projectId, sessionId, events, CancellationToken.None);

        // The secret text must really reach the consolidator, or the absence asserted below proves nothing.
        consolidator.Verify(
            c => c.ConsolidateAsync(
                projectId, sessionId,
                It.Is<MemoryCandidate>(cand => cand.Title == SecretTitle
                    && cand.Body.Contains("SECRET_BODY_57f3d2", StringComparison.Ordinal)
                    && !cand.Body.Contains("CONFIDENCE", StringComparison.Ordinal)
                    && cand.Source == EvidenceSource.EventBatch),
                false, It.IsAny<CancellationToken>()),
            Times.Once);

        logger.Messages.Should().HaveCountGreaterThanOrEqualTo(2);
        logger.Messages.Should().Contain(m => m.Contains("candidate memories", StringComparison.Ordinal));
        logger.Messages.Should().Contain(m => m.Contains("1 created, 0 reinforced, 0 merged, 0 superseded, 0 contested, 0 events", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains(SecretTitle, StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains("SECRET_BODY_57f3d2", StringComparison.Ordinal));
    }

    /// <summary>
    /// A chat client that throws mid-call is a transient provider failure, and the only component
    /// that can recover it is <c>ExtractMemoryJobHandler</c>'s retry/backoff — which never sees a
    /// swallowed exception. So the failure is logged and rethrown: the batch's memories are retried
    /// later rather than lost to a job that reported success having extracted nothing.
    /// </summary>
    [Fact]
    public async Task ExtractFromEventsAsync_rethrows_an_exception_from_the_chat_client_so_the_job_can_retry()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var events = MakeEvents(sessionId);

        var consolidator = new Mock<IMemoryConsolidator>();
        var resolver = new FakeProviderResolver(new ThrowingChatClient());
        var logger = new CapturingLogger<LlmMemoryExtractor>();
        var sut = new LlmMemoryExtractor(consolidator.Object, resolver, new FakeUsageRecorder(), logger);

        var act = async () => await sut.ExtractFromEventsAsync(projectId, sessionId, events, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        consolidator.Verify(
            c => c.ConsolidateAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<MemoryCandidate>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        logger.WarningMessages.Should().Contain(m => m.Contains("failed to extract candidate memories", StringComparison.Ordinal));
    }

    /// <summary>A response in none of the TYPE:/TITLE:/BODY: block format parses to zero candidates and
    /// never reaches the consolidator.</summary>
    [Theory]
    [InlineData("This is just some unrelated prose the model returned instead of the expected block format.")]
    [InlineData("")]
    public async Task ExtractFromEventsAsync_stores_no_memories_when_the_llm_response_is_unparseable(string garbageResponse)
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var events = MakeEvents(sessionId);

        var consolidator = new Mock<IMemoryConsolidator>();
        var resolver = new FakeProviderResolver(new FakeChatClient(garbageResponse));
        var logger = new CapturingLogger<LlmMemoryExtractor>();
        var sut = new LlmMemoryExtractor(consolidator.Object, resolver, new FakeUsageRecorder(), logger);

        var result = await sut.ExtractFromEventsAsync(projectId, sessionId, events, CancellationToken.None);

        result.Should().BeEmpty();
        consolidator.Verify(
            c => c.ConsolidateAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<MemoryCandidate>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static string CandidateResponse() =>
        $"""
        TYPE: Decision
        TITLE: {SecretTitle}
        BODY: {SecretBody}
        CONFIDENCE: 0.9
        END_MEMORY
        """;

    private static SessionEvent[] MakeEvents(Guid sessionId) =>
    [
        new() { SessionId = sessionId, Type = SessionEventType.FileEdit, Timestamp = DateTimeOffset.UtcNow, PayloadJson = """{"toolName":"Edit","path":"src/Foo.cs"}""" },
    ];

    private static Context.Domain.Entities.Memory MakeMemory(Guid projectId, MemoryCandidate candidate) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = candidate.Type,
        Title = candidate.Title,
        Body = candidate.Body,
        Tier = MemoryTier.Working,
        CreatedAt = DateTimeOffset.UtcNow,
        LastReinforcedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Always resolves to <paramref name="chatClient"/>; this test never exercises
    /// embedding generation (the consolidator that would call it is mocked), so
    /// <see cref="GetEmbeddingGeneratorAsync"/> throws if it's ever unexpectedly reached.</summary>
    private sealed class FakeProviderResolver(IChatClient chatClient) : IProviderResolver
    {
        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException("LlmMemoryExtractor doesn't need an embedding generator directly.");

        public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) => Task.FromResult(chatClient);
    }

    /// <summary>Always returns <paramref name="response"/>, regardless of the prompt. Reports
    /// non-"noop" provider metadata so <c>IsNoOp()</c> is false and <see cref="LlmMemoryExtractor"/>
    /// takes the real LLM-candidate-generation path rather than the offline heuristic.</summary>
    private sealed class FakeChatClient(string response) : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("fake", null, "fake-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeUsageRecorder : IUsageRecorder
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A reachable (non-"noop") provider that fails mid-call.</summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("fake", null, "fake-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated provider failure.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    /// <summary>Captures every formatted message logged through it, so a test can assert on log
    /// *content* directly instead of only on structured state/message templates.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public List<string> WarningMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            if (logLevel == LogLevel.Warning)
            {
                WarningMessages.Add(message);
            }
        }
    }
}
