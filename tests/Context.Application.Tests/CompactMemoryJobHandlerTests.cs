using System.Text.Json;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Context.Application.Tests;

public sealed class CompactMemoryJobHandlerTests
{
    private static readonly float[] SimilarEmbedding = [1f, 0f];
    private static readonly float[] OrthogonalEmbedding = [0f, 1f];

    [Fact]
    public async Task HandleAsync_merges_two_similar_memories_preserving_the_union_of_their_facts()
    {
        var projectId = Guid.NewGuid();
        var store = new FakeMemoryStore();

        var memoryA = MakeMemory(projectId, "Memory A", "Body A", facts: ["fact-a"]);
        var memoryB = MakeMemory(projectId, "Memory B", "Body B", facts: ["fact-b"]);
        await store.SaveAsync(memoryA, SimilarEmbedding, CancellationToken.None);
        await store.SaveAsync(memoryB, SimilarEmbedding, CancellationToken.None);

        var settingsService = SettingsServiceReturning(projectId, 0.85);
        var resolver = new FakeProviderResolver(MergeResponse("Merged Title", "Merged body"), SimilarEmbedding);
        var sut = new CompactMemoryJobHandler(store, settingsService.Object, resolver, new FakeUsageRecorder());

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        var merged = store.Saved.Select(s => s.Memory).Single(m => m.IsCompacted);
        merged.Title.Should().Be("Merged Title");
        merged.Body.Should().Be("Merged body");

        store.Superseded.Should().HaveCount(2);
        store.Superseded.Should().OnlyContain(s => s.NewId == merged.Id);
        store.Superseded.Select(s => s.OldId).Should().BeEquivalentTo([memoryA.Id, memoryB.Id]);

        var facts = JsonSerializer.Deserialize<List<string>>(merged.FactsJson!)!;
        facts.Should().BeEquivalentTo(["fact-a", "fact-b"]);
    }

    [Fact]
    public async Task HandleAsync_never_touches_pinned_or_already_compacted_memories_even_when_similar_to_others()
    {
        var projectId = Guid.NewGuid();
        var store = new FakeMemoryStore();

        var pinned = MakeMemory(projectId, "Pinned", "Pinned body");
        pinned.Pinned = true;
        var compacted = MakeMemory(projectId, "Compacted", "Compacted body");
        compacted.IsCompacted = true;
        var normalA = MakeMemory(projectId, "Normal A", "Normal body A");
        var normalB = MakeMemory(projectId, "Normal B", "Normal body B");

        // All four share the same embedding, so similarity alone would cluster every one of them
        // together - the pinned/compacted exclusion must happen before clustering even runs.
        await store.SaveAsync(pinned, SimilarEmbedding, CancellationToken.None);
        await store.SaveAsync(compacted, SimilarEmbedding, CancellationToken.None);
        await store.SaveAsync(normalA, SimilarEmbedding, CancellationToken.None);
        await store.SaveAsync(normalB, SimilarEmbedding, CancellationToken.None);

        var settingsService = SettingsServiceReturning(projectId, 0.85);
        var resolver = new FakeProviderResolver(MergeResponse("Merged", "Merged body"), SimilarEmbedding);
        var sut = new CompactMemoryJobHandler(store, settingsService.Object, resolver, new FakeUsageRecorder());

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        store.Superseded.Should().HaveCount(2);
        store.Superseded.Select(s => s.OldId).Should().BeEquivalentTo([normalA.Id, normalB.Id]);
        store.Superseded.Should().NotContain(s => s.OldId == pinned.Id || s.OldId == compacted.Id);
        pinned.SupersededById.Should().BeNull();
        compacted.SupersededById.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_leaves_singleton_clusters_completely_untouched()
    {
        var projectId = Guid.NewGuid();
        var store = new FakeMemoryStore();

        var soloA = MakeMemory(projectId, "Solo A", "Solo body A");
        var soloB = MakeMemory(projectId, "Solo B", "Solo body B");
        await store.SaveAsync(soloA, SimilarEmbedding, CancellationToken.None);
        await store.SaveAsync(soloB, OrthogonalEmbedding, CancellationToken.None);

        var settingsService = SettingsServiceReturning(projectId, 0.85);
        var resolver = new FakeProviderResolver(MergeResponse("Merged", "Merged body"), SimilarEmbedding);
        var sut = new CompactMemoryJobHandler(store, settingsService.Object, resolver, new FakeUsageRecorder());

        var savedCountBeforeRun = store.Saved.Count;

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        store.Saved.Count.Should().Be(savedCountBeforeRun);
        store.Superseded.Should().BeEmpty();
    }

    private static Mock<IProjectSettingsService> SettingsServiceReturning(Guid projectId, double similarityThreshold)
    {
        var settingsService = new Mock<IProjectSettingsService>();
        settingsService.Setup(s => s.GetAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SettingsWithThreshold(similarityThreshold));
        return settingsService;
    }

    private static ProjectSettingsDto SettingsWithThreshold(double threshold) => new(
        EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null,
        OllamaChatModel: null, ReindexEnabled: false, ReindexIntervalMinutes: 60, LastReindexScheduledAt: null,
        MemoryCompactionSimilarityThreshold: threshold);

    private static string MergeResponse(string title, string body) => $"TITLE: {title}\nBODY: {body}";

    private static Job MakeJob(Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.CompactMemory,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(new CompactMemoryPayload(projectId)),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static Context.Domain.Entities.Memory MakeMemory(Guid projectId, string title, string body, List<string>? facts = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = MemoryType.Discovery,
        Title = title,
        Body = body,
        FactsJson = facts is null ? null : JsonSerializer.Serialize(facts),
        Confidence = 0.7,
        Tier = MemoryTier.Working,
        Pinned = false,
        ReinforcementCount = 0,
        SupersededById = null,
        IsCompacted = false,
        CreatedFromSessionId = null,
        CreatedAt = DateTimeOffset.UtcNow,
        LastReinforcedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Always resolves to the one fixed chat client / embedding generator it was
    /// constructed with, regardless of projectId - same shape as
    /// ExtractMemoryJobHandlerTests.FakeProviderResolver.</summary>
    private sealed class FakeProviderResolver(string chatResponseText, float[] embeddingVector) : IProviderResolver
    {
        private readonly IChatClient _chatClient = new FakeChatClient(chatResponseText);
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = new FakeEmbeddingGenerator(embeddingVector);

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(_embeddingGenerator);

        public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) => Task.FromResult(_chatClient);
    }

    /// <summary>Always returns <paramref name="responseText"/> regardless of the incoming
    /// messages - CompactMemoryJobHandler only ever sends one kind of request (the merge prompt),
    /// unlike MemoryConsolidator's chat client which also fields consistency checks.</summary>
    private sealed class FakeChatClient(string responseText) : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("fake", null, "fake-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    /// <summary>Always returns <paramref name="vector"/>, regardless of the input text.</summary>
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
    // declare an IUsageRecorder - CompactMemoryJobHandler needs the Context.Memory one.
    private sealed class FakeUsageRecorder : Context.Memory.Abstractions.IUsageRecorder
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>In-memory <see cref="IMemoryStore"/> that actually accumulates saved memories (and
    /// returns them from <see cref="GetExistingWithEmbeddingsAsync"/>) - same shape as
    /// ExtractMemoryJobHandlerTests.FakeMemoryStore.</summary>
    private sealed class FakeMemoryStore : IMemoryStore
    {
        private readonly Dictionary<Guid, (Context.Domain.Entities.Memory Memory, float[] Embedding)> _byId = [];

        public List<(Context.Domain.Entities.Memory Memory, float[]? Embedding)> Saved { get; } = [];
        public List<(Guid OldId, Guid NewId)> Superseded { get; } = [];

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
    }
}
