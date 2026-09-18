using System.Text.Json;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Context.Application.Tests;

/// <summary>
/// MEMORY_PIPELINE_PLAN.md §D.6/Gap #6 + §H test #13: reprocessing the SAME
/// <see cref="SessionSummaryChunk"/> twice through <see cref="ExtractMemoryJobHandler"/> (e.g. a
/// retried job after a transient failure elsewhere in the pipeline) must not produce two
/// near-identical <see cref="Memory"/> rows for clearly-identical content — <c>MemoryConsolidator</c>'s
/// existing &gt;0.92-similarity reinforce path should absorb the second pass instead of inserting a
/// duplicate. This is existing behavior (an accidental-but-real property of similarity-based
/// dedup, not a designed idempotency guarantee — see the comment on
/// <see cref="ExtractMemoryJobHandler"/>), so this test wires up the real
/// <see cref="LlmMemoryExtractor"/> + <see cref="MemoryConsolidator"/> pipeline rather than mocking
/// <see cref="IMemoryExtractor"/> away — mocking it out would test nothing, since the dedup logic
/// being characterized here doesn't live in <see cref="ExtractMemoryJobHandler"/> itself.
/// </summary>
public sealed class ExtractMemoryJobHandlerTests
{
    private static readonly float[] FixedEmbedding = [1f, 0f];

    [Fact]
    public async Task HandleAsync_reprocessing_the_same_chunk_reinforces_instead_of_duplicating_an_identical_memory()
    {
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var chunk = MakeChunk(sessionId);

        var sessionRepository = new Mock<ISessionRepository>();
        sessionRepository.Setup(r => r.GetSummaryChunkByIdAsync(projectId, chunk.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunk);
        sessionRepository.Setup(r => r.GetByIdAsync(projectId, sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Session?)null);

        var store = new FakeMemoryStore();
        var resolver = new FakeProviderResolver(CandidateResponse());
        var consolidator = new MemoryConsolidator(store, resolver, new FakeUsageRecorder(), new FakeSettingsProvider());
        var extractor = new LlmMemoryExtractor(store, consolidator, resolver, new FakeUsageRecorder());
        var sut = new ExtractMemoryJobHandler(extractor, sessionRepository.Object);

        // Same chunk, processed twice - simulating a retried ExtractMemory job.
        await sut.HandleAsync(MakeJob(projectId, chunk.Id), CancellationToken.None);
        await sut.HandleAsync(MakeJob(projectId, chunk.Id), CancellationToken.None);

        // Exactly one fresh row (the first pass's insert) - the second pass's identical candidate
        // must land on the reinforce path, not insert a second near-duplicate memory.
        store.Saved.Count(s => s.Embedding is not null).Should().Be(1);
        store.Saved.Select(s => s.Memory.Id).Distinct().Should().ContainSingle();
        store.Superseded.Should().BeEmpty();

        // The reinforce path itself did run (not just "nothing happened the second time") -
        // ReinforcementCount on the one memory should reflect the second pass.
        var persisted = store.Saved.Select(s => s.Memory).Distinct().Single();
        persisted.ReinforcementCount.Should().Be(1);
    }

    private static Job MakeJob(Guid projectId, Guid summaryChunkId) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.ExtractMemory,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(new ExtractMemoryPayload(Guid.NewGuid(), summaryChunkId)),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static SessionSummaryChunk MakeChunk(Guid sessionId) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        Title = "Decided to use SQLite for local caching",
        EventCount = 20,
        FromEventAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        ToEventAt = DateTimeOffset.UtcNow,
        GeneratedAt = DateTimeOffset.UtcNow
    };

    // Same TYPE/TITLE/BODY every time this "LLM" is asked to extract - the point of this test is
    // that identical content, reprocessed, must not duplicate.
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

    /// <summary>Returns <paramref name="candidateResponse"/> for a candidate-extraction request,
    /// or "CONSISTENT" for a consistency-check request (identified by the "CONTRADICTORY" marker
    /// unique to <c>MemoryConsistencyChecker.SystemPrompt</c>, since that internal class isn't
    /// visible from this assembly) - so the second HandleAsync pass's identical candidate is
    /// judged a restatement and reinforces rather than superseding or inserting fresh.</summary>
    private sealed class FakeChatClient(string candidateResponse) : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("fake", null, "fake-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var systemText = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty;
            var isConsistencyCheck = systemText.Contains("CONTRADICTORY", StringComparison.Ordinal);
            var text = isConsistencyCheck ? "CONSISTENT" : candidateResponse;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    /// <summary>Always returns <paramref name="vector"/>, regardless of the input text, so the
    /// candidate's embedding is bit-identical across both HandleAsync passes and lands at cosine
    /// similarity 1.0 against the memory the first pass inserted.</summary>
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

    // Fully qualified: Context.Infrastructure.Abstractions and Context.Memory.Abstractions both
    // declare an IUsageRecorder (mirrors of each other, kept separate so Context.Memory has no
    // dependency on Context.Infrastructure) - LlmMemoryExtractor/MemoryConsolidator need the
    // Context.Memory one.
    private sealed class FakeUsageRecorder : Context.Memory.Abstractions.IUsageRecorder
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSettingsProvider : IMemoryConsolidationSettingsProvider
    {
        public Task<MemoryConsolidationSettings> GetAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(new MemoryConsolidationSettings());
    }

    /// <summary>In-memory <see cref="IMemoryStore"/> that actually accumulates saved memories (and
    /// returns them from <see cref="GetExistingWithEmbeddingsAsync"/>) so the second HandleAsync
    /// pass in this test sees what the first one inserted - unlike MemoryConsolidatorTests's fake,
    /// which is handed a fixed "existing" list per call and doesn't need this.</summary>
    private sealed class FakeMemoryStore : IMemoryStore
    {
        // Fully qualified: bare "Memory" resolves to the Context.Memory namespace here (this file's
        // enclosing Context.* namespace makes it visible unqualified), not Context.Domain.Entities.Memory.
        private readonly Dictionary<Guid, (Context.Domain.Entities.Memory Memory, float[] Embedding)> _byId = [];

        public List<(Context.Domain.Entities.Memory Memory, float[]? Embedding)> Saved { get; } = [];
        public List<(Guid OldId, Guid NewId)> Superseded { get; } = [];
        public List<Guid> Reinforced { get; } = [];

        public Task<IReadOnlyList<ExistingMemoryWithEmbedding>> GetExistingWithEmbeddingsAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingMemoryWithEmbedding>>(
                _byId.Values
                    .Where(v => v.Memory.ProjectId == projectId)
                    .Select(v => new ExistingMemoryWithEmbedding(v.Memory, v.Embedding))
                    .ToList());

        public Task SaveAsync(Context.Domain.Entities.Memory memory, float[]? embedding, CancellationToken ct)
        {
            Saved.Add((memory, embedding));
            if (embedding is not null)
            {
                _byId[memory.Id] = (memory, embedding);
            }

            return Task.CompletedTask;
        }

        public Task MarkSupersededAsync(Guid oldMemoryId, Guid newMemoryId, CancellationToken ct)
        {
            Superseded.Add((oldMemoryId, newMemoryId));
            if (_byId.TryGetValue(oldMemoryId, out var old))
            {
                old.Memory.SupersededById = newMemoryId;
            }

            return Task.CompletedTask;
        }

        public Task SaveMergedAsync(Context.Domain.Entities.Memory merged, float[] embedding, IReadOnlyCollection<Guid> supersededIds, CancellationToken ct) =>
            throw new NotSupportedException("Only CompactMemoryJobHandler merges memories.");

        public Task<int?> ReinforceAsync(Guid memoryId, DateTimeOffset reinforcedAt, int promotionReinforcementThreshold, CancellationToken ct)
        {
            if (!_byId.TryGetValue(memoryId, out var entry))
            {
                return Task.FromResult<int?>(null);
            }

            entry.Memory.ReinforcementCount++;
            entry.Memory.LastReinforcedAt = reinforcedAt;
            if (entry.Memory.Pinned || entry.Memory.ReinforcementCount >= promotionReinforcementThreshold)
            {
                entry.Memory.Tier = Context.Domain.Enums.MemoryTier.LongTerm;
            }

            Reinforced.Add(memoryId);
            return Task.FromResult<int?>(entry.Memory.ReinforcementCount);
        }
    }
}
