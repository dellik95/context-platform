using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Context.Memory.Tests;

/// <summary>
/// Characterizes <see cref="MemoryConsolidator"/> — the dedup/reinforce/supersede/
/// restatement-walk decision moved verbatim out of <c>LlmMemoryExtractor.ExtractAsync</c>
/// (Phase 1 of docs/architecture/MEMORY_PIPELINE_PLAN.md). Embeddings are two-dimensional unit
/// vectors constructed so <see cref="CosineSimilarity"/> between the candidate and an existing
/// memory equals an exact, chosen value — see <see cref="EmbeddingAtSimilarity"/> — so each test
/// lands precisely on one side of the 0.80/0.92 thresholds rather than hoping a hash-based fake
/// embedding happens to land there.
/// </summary>
public class MemoryConsolidatorTests
{
    private static readonly float[] CandidateEmbedding = [1f, 0f];

    [Fact]
    public async Task ConsolidateAsync_inserts_a_fresh_memory_when_the_best_match_is_below_the_low_similarity_floor()
    {
        var projectId = Guid.NewGuid();
        var oldMemory = ExistingMemory();
        var existing = new List<ExistingMemoryWithEmbedding> { new(oldMemory, EmbeddingAtSimilarity(0.5)) };
        var store = new FakeMemoryStore();
        var sut = new MemoryConsolidator(store, FakeResolver("CONSISTENT"), new FakeUsageRecorder());

        var result = await sut.ConsolidateAsync(projectId, Guid.NewGuid(), Candidate(), existing, CancellationToken.None);

        result.Id.Should().NotBe(oldMemory.Id);
        result.Tier.Should().Be(MemoryTier.Working);
        result.ReinforcementCount.Should().Be(0);
        store.Saved.Should().ContainSingle(s => s.Memory.Id == result.Id && s.Embedding != null);
        store.Superseded.Should().BeEmpty();
        existing.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConsolidateAsync_reinforces_the_existing_memory_when_similarity_exceeds_0_92_and_is_judged_consistent()
    {
        var projectId = Guid.NewGuid();
        var oldMemory = ExistingMemory();
        var existing = new List<ExistingMemoryWithEmbedding> { new(oldMemory, EmbeddingAtSimilarity(0.95)) };
        var store = new FakeMemoryStore();
        var sut = new MemoryConsolidator(store, FakeResolver("CONSISTENT"), new FakeUsageRecorder());

        var result = await sut.ConsolidateAsync(projectId, Guid.NewGuid(), Candidate(), existing, CancellationToken.None);

        result.Id.Should().Be(oldMemory.Id);
        result.ReinforcementCount.Should().Be(1);
        result.Tier.Should().Be(MemoryTier.Working); // one reinforcement, below the promotion threshold
        store.Saved.Should().ContainSingle(s => s.Memory.Id == oldMemory.Id && s.Embedding == null);
        store.Superseded.Should().BeEmpty();
        existing.Should().HaveCount(1); // reinforcement doesn't grow the dedup set
    }

    [Fact]
    public async Task ConsolidateAsync_supersedes_the_existing_memory_when_similarity_exceeds_0_92_and_is_judged_contradictory()
    {
        var projectId = Guid.NewGuid();
        var oldMemory = ExistingMemory();
        var existing = new List<ExistingMemoryWithEmbedding> { new(oldMemory, EmbeddingAtSimilarity(0.95)) };
        var store = new FakeMemoryStore();
        var sut = new MemoryConsolidator(store, FakeResolver("CONTRADICTORY"), new FakeUsageRecorder());

        var result = await sut.ConsolidateAsync(projectId, Guid.NewGuid(), Candidate(), existing, CancellationToken.None);

        result.Id.Should().NotBe(oldMemory.Id);
        store.Saved.Should().ContainSingle(s => s.Memory.Id == result.Id && s.Embedding != null);
        store.Superseded.Should().ContainSingle(s => s.OldId == oldMemory.Id && s.NewId == result.Id);
        existing.Should().HaveCount(2); // old memory kept (not deleted), new one appended
    }

    [Fact]
    public async Task ConsolidateAsync_reinforces_via_the_restatement_walk_when_similarity_is_in_the_0_80_to_0_92_band_and_is_judged_a_restatement()
    {
        var projectId = Guid.NewGuid();
        var oldMemory = ExistingMemory();
        var existing = new List<ExistingMemoryWithEmbedding> { new(oldMemory, EmbeddingAtSimilarity(0.85)) };
        var store = new FakeMemoryStore();
        var sut = new MemoryConsolidator(store, FakeResolver("CONSISTENT"), new FakeUsageRecorder());

        var result = await sut.ConsolidateAsync(projectId, Guid.NewGuid(), Candidate(), existing, CancellationToken.None);

        result.Id.Should().Be(oldMemory.Id);
        result.ReinforcementCount.Should().Be(1);
        store.Saved.Should().ContainSingle(s => s.Memory.Id == oldMemory.Id && s.Embedding == null);
        store.Superseded.Should().BeEmpty();
        existing.Should().HaveCount(1);
    }

    [Fact]
    public async Task ConsolidateAsync_inserts_fresh_without_superseding_when_the_band_match_is_not_judged_a_restatement()
    {
        // A band-range match ([0.80, 0.92]) that fails the restatement check is genuinely new —
        // unlike the >0.92 case, nothing in this band was judged to contradict the old memory
        // either, so it must NOT be marked superseded.
        var projectId = Guid.NewGuid();
        var oldMemory = ExistingMemory();
        var existing = new List<ExistingMemoryWithEmbedding> { new(oldMemory, EmbeddingAtSimilarity(0.85)) };
        var store = new FakeMemoryStore();
        var sut = new MemoryConsolidator(store, FakeResolver("CONTRADICTORY"), new FakeUsageRecorder());

        var result = await sut.ConsolidateAsync(projectId, Guid.NewGuid(), Candidate(), existing, CancellationToken.None);

        result.Id.Should().NotBe(oldMemory.Id);
        result.ReinforcementCount.Should().Be(0);
        store.Saved.Should().ContainSingle(s => s.Memory.Id == result.Id && s.Embedding != null);
        store.Superseded.Should().BeEmpty();
        existing.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConsolidateAsync_promotes_to_LongTerm_once_reinforcement_count_reaches_the_promotion_threshold()
    {
        var projectId = Guid.NewGuid();
        var oldMemory = ExistingMemory(reinforcementCount: 1); // one prior reinforcement already
        var existing = new List<ExistingMemoryWithEmbedding> { new(oldMemory, EmbeddingAtSimilarity(0.95)) };
        var store = new FakeMemoryStore();
        var sut = new MemoryConsolidator(store, FakeResolver("CONSISTENT"), new FakeUsageRecorder());

        var result = await sut.ConsolidateAsync(projectId, Guid.NewGuid(), Candidate(), existing, CancellationToken.None);

        result.ReinforcementCount.Should().Be(2);
        result.Tier.Should().Be(MemoryTier.LongTerm);
    }

    [Fact]
    public async Task ConsolidateAsync_promotes_to_LongTerm_immediately_when_the_reinforced_memory_is_pinned()
    {
        var projectId = Guid.NewGuid();
        var oldMemory = ExistingMemory(pinned: true); // Pinned, but only its first reinforcement
        var existing = new List<ExistingMemoryWithEmbedding> { new(oldMemory, EmbeddingAtSimilarity(0.95)) };
        var store = new FakeMemoryStore();
        var sut = new MemoryConsolidator(store, FakeResolver("CONSISTENT"), new FakeUsageRecorder());

        var result = await sut.ConsolidateAsync(projectId, Guid.NewGuid(), Candidate(), existing, CancellationToken.None);

        result.ReinforcementCount.Should().Be(1); // below the 2-reinforcement threshold on its own
        result.Tier.Should().Be(MemoryTier.LongTerm); // promoted anyway because Pinned
    }

    /// <summary>Unit vector at exact cosine similarity <paramref name="similarity"/> to
    /// <see cref="CandidateEmbedding"/> (1, 0) — dot(A,B)/(|A||B|) = similarity when both are
    /// unit length, giving deterministic control over which threshold branch a test exercises.</summary>
    private static float[] EmbeddingAtSimilarity(double similarity) =>
        [(float)similarity, (float)Math.Sqrt(Math.Max(0, 1 - similarity * similarity))];

    private static MemoryCandidate Candidate() => new(
        Type: MemoryType.Decision,
        Title: "New candidate title",
        Body: "New candidate body.",
        Confidence: 0.8,
        Facts: [],
        Concepts: [],
        FilesRead: [],
        FilesModified: []);

    private static Context.Domain.Entities.Memory ExistingMemory(bool pinned = false, int reinforcementCount = 0) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Type = MemoryType.Decision,
        Title = "Old memory title",
        Body = "Old memory body.",
        Tier = MemoryTier.Working,
        Pinned = pinned,
        ReinforcementCount = reinforcementCount,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        LastReinforcedAt = DateTimeOffset.UtcNow.AddDays(-1)
    };

    private static FakeProviderResolver FakeResolver(string consistencyResponse) =>
        new(new FakeChatClient(consistencyResponse), new FakeEmbeddingGenerator(CandidateEmbedding));

    /// <summary>Always resolves to the one fixed chat client / embedding generator it was
    /// constructed with, regardless of projectId — these tests don't exercise per-project
    /// provider selection.</summary>
    private sealed class FakeProviderResolver(IChatClient chatClient, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : IProviderResolver
    {
        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(embeddingGenerator);

        public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) => Task.FromResult(chatClient);
    }

    /// <summary>Always returns <paramref name="response"/> to the consistency-check prompt.
    /// Reports non-"noop" provider metadata so <c>IsNoOp()</c> is false and
    /// <see cref="MemoryConsolidator"/> takes the real (LLM-judged) consistency path rather than
    /// the offline heuristic.</summary>
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

    /// <summary>Always returns <paramref name="vector"/>, regardless of the input text — lets
    /// tests fix the candidate's embedding so its cosine similarity to a hand-picked existing
    /// embedding (<see cref="EmbeddingAtSimilarity"/>) is exact.</summary>
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

    private sealed class FakeMemoryStore : IMemoryStore
    {
        public List<(Context.Domain.Entities.Memory Memory, float[]? Embedding)> Saved { get; } = [];
        public List<(Guid OldId, Guid NewId)> Superseded { get; } = [];

        public Task<IReadOnlyList<ExistingMemoryWithEmbedding>> GetExistingWithEmbeddingsAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException("MemoryConsolidator takes its existing-memory set as a parameter; it does not fetch it itself.");

        public Task SaveAsync(Context.Domain.Entities.Memory memory, float[]? embedding, CancellationToken ct)
        {
            Saved.Add((memory, embedding));
            return Task.CompletedTask;
        }

        public Task MarkSupersededAsync(Guid oldMemoryId, Guid newMemoryId, CancellationToken ct)
        {
            Superseded.Add((oldMemoryId, newMemoryId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUsageRecorder : IUsageRecorder
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
    }
}
