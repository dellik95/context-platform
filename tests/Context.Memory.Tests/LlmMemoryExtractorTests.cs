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
/// MEMORY_PIPELINE_PLAN.md §D.5/Gap #5 + §H test #14: <see cref="LlmMemoryExtractor.ExtractAsync"/>'s
/// new structured `LogInformation` calls (candidates-extracted count, consolidation summary) must
/// carry only counts/IDs, never the candidate's own Title/Body text — the same content-safety
/// discipline this class's pre-existing `LogWarning` calls already follow. <see cref="IMemoryConsolidator"/>
/// is mocked here (its own decision logic is covered by <see cref="MemoryConsolidatorTests"/>); this
/// test is only about what <c>LlmMemoryExtractor</c> itself writes to the logger.
/// </summary>
public class LlmMemoryExtractorTests
{
    private const string SecretTitle = "SECRET_TITLE_57f3d2 database migration notes";
    private const string SecretBody = "SECRET_BODY_57f3d2 rotated the prod credentials for the payments service";

    [Fact]
    public async Task ExtractAsync_logs_counts_only_and_never_leaks_the_candidates_title_or_body_text()
    {
        var projectId = Guid.NewGuid();
        var chunk = MakeChunk();

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.GetExistingWithEmbeddingsAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ExistingMemoryWithEmbedding>)[]);

        var consolidator = new Mock<IMemoryConsolidator>();
        consolidator
            .Setup(c => c.ConsolidateAsync(projectId, chunk.SessionId, It.IsAny<MemoryCandidate>(), It.IsAny<List<ExistingMemoryWithEmbedding>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid? _, MemoryCandidate cand, List<ExistingMemoryWithEmbedding> _, bool _, CancellationToken _) =>
                new ConsolidationResult(MakeMemory(projectId, cand), ConsolidationOutcome.Created));

        var resolver = new FakeProviderResolver(new FakeChatClient(CandidateResponse()));
        var logger = new CapturingLogger<LlmMemoryExtractor>();
        var sut = new LlmMemoryExtractor(store.Object, consolidator.Object, resolver, new FakeUsageRecorder(), logger);

        await sut.ExtractAsync(projectId, chunk, CancellationToken.None);

        // Prove the secret text really did flow through this call (via the parsed candidate),
        // so the absence asserted below is a meaningful content-safety guarantee and not just an
        // empty-log false negative.
        consolidator.Verify(
            c => c.ConsolidateAsync(
                projectId, chunk.SessionId,
                It.Is<MemoryCandidate>(cand => cand.Title == SecretTitle && cand.Body.Contains("SECRET_BODY_57f3d2", StringComparison.Ordinal)),
                It.IsAny<List<ExistingMemoryWithEmbedding>>(), false, It.IsAny<CancellationToken>()),
            Times.Once);

        logger.Messages.Should().HaveCountGreaterThanOrEqualTo(2);
        logger.Messages.Should().Contain(m => m.Contains("candidate memories", StringComparison.Ordinal));
        logger.Messages.Should().Contain(m => m.Contains("1 created, 0 reinforced, 0 superseded", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains(SecretTitle, StringComparison.Ordinal));
        logger.Messages.Should().NotContain(m => m.Contains("SECRET_BODY_57f3d2", StringComparison.Ordinal));
    }

    /// <summary>
    /// A chat client that throws mid-call is a transient provider failure, and the only component
    /// that can recover it is <c>ExtractMemoryJobHandler</c>'s retry/backoff — which never sees a
    /// swallowed exception. So the failure is logged and rethrown: the chunk's memories are retried
    /// later rather than lost to a job that reported success having extracted nothing.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_rethrows_an_exception_from_the_chat_client_so_the_job_can_retry()
    {
        var projectId = Guid.NewGuid();
        var chunk = MakeChunk();

        var store = new Mock<IMemoryStore>();
        var consolidator = new Mock<IMemoryConsolidator>();
        var resolver = new FakeProviderResolver(new ThrowingChatClient());
        var logger = new CapturingLogger<LlmMemoryExtractor>();
        var sut = new LlmMemoryExtractor(store.Object, consolidator.Object, resolver, new FakeUsageRecorder(), logger);

        var act = async () => await sut.ExtractAsync(projectId, chunk, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        consolidator.Verify(
            c => c.ConsolidateAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<MemoryCandidate>(), It.IsAny<List<ExistingMemoryWithEmbedding>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        logger.WarningMessages.Should().Contain(m => m.Contains("failed to extract candidate memories", StringComparison.Ordinal));
    }

    /// <summary>
    /// MEMORY_PIPELINE_PLAN.md §H case #8: invalid LLM response → no malformed memory stored,
    /// proven end-to-end through <see cref="LlmMemoryExtractor.ExtractAsync"/> rather than only at
    /// the <see cref="MemoryCandidateParser"/> unit level (already covered by
    /// <c>MemoryCandidateParserTests</c>). A response that matches none of the
    /// TYPE:/TITLE:/BODY:/... block format must parse to zero candidates, so extraction returns no
    /// memories and never reaches the consolidator.
    /// </summary>
    [Theory]
    [InlineData("This is just some unrelated prose the model returned instead of the expected block format.")]
    [InlineData("")]
    public async Task ExtractAsync_stores_no_memories_when_the_llm_response_is_unparseable(string garbageResponse)
    {
        var projectId = Guid.NewGuid();
        var chunk = MakeChunk();

        var store = new Mock<IMemoryStore>();
        var consolidator = new Mock<IMemoryConsolidator>();
        var resolver = new FakeProviderResolver(new FakeChatClient(garbageResponse));
        var logger = new CapturingLogger<LlmMemoryExtractor>();
        var sut = new LlmMemoryExtractor(store.Object, consolidator.Object, resolver, new FakeUsageRecorder(), logger);

        var result = await sut.ExtractAsync(projectId, chunk, CancellationToken.None);

        result.Should().BeEmpty();
        consolidator.Verify(
            c => c.ConsolidateAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<MemoryCandidate>(), It.IsAny<List<ExistingMemoryWithEmbedding>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(s => s.GetExistingWithEmbeddingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static string CandidateResponse() =>
        $"""
        TYPE: Decision
        TITLE: {SecretTitle}
        BODY: {SecretBody}
        CONFIDENCE: 0.9
        END_MEMORY
        """;

    private static SessionSummaryChunk MakeChunk() => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        Title = "Some session work",
        EventCount = 20,
        FromEventAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        ToEventAt = DateTimeOffset.UtcNow,
        GeneratedAt = DateTimeOffset.UtcNow
    };

    private static Context.Domain.Entities.Memory MakeMemory(Guid projectId, MemoryCandidate candidate) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = candidate.Type,
        Title = candidate.Title,
        Body = candidate.Body,
        Confidence = candidate.Confidence,
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
            throw new NotSupportedException("LlmMemoryExtractor.ExtractAsync doesn't need an embedding generator directly.");

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

    /// <summary>Always throws from <see cref="GetResponseAsync"/>, simulating a provider that is
    /// unreachable/erroring mid-call (§H case #7) — distinct from <see cref="FakeChatClient"/>,
    /// which always succeeds, and from the <c>IsNoOp()</c> fallback, which never calls the client
    /// at all. Reports the same non-"noop" provider metadata so <c>ExtractAsync</c> takes the real
    /// LLM path before hitting the failure.</summary>
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
