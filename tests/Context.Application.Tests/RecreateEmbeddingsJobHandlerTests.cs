using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Context.Application.Tests;

/// <summary>
/// Tests for <see cref="RecreateEmbeddingsJobHandler"/>'s three defining behaviors: it never gates
/// on an existing <c>(ContentHash, Provider)</c> cache row (that's the whole point of a *force*
/// regeneration job — see <see cref="EmbedChunkJobHandler"/> for the handler that DOES cache-gate),
/// every chunk id sharing one content hash gets its own vector-index upsert, and every live
/// knowledge memory gets re-embedded (events are never embedded).
/// </summary>
public sealed class RecreateEmbeddingsJobHandlerTests
{
    private static readonly float[] FixedVector = [0.5f, 0.25f, 0.1f];

    [Fact]
    public async Task HandleAsync_upserts_the_embedding_for_every_distinct_content_hash_even_when_a_cached_row_already_exists()
    {
        var projectId = Guid.NewGuid();
        var chunkA = MakeChunk(contentHash: "hash-a", text: "alpha text");
        var chunkB = MakeChunk(contentHash: "hash-b", text: "beta text");

        var chunkRepository = new Mock<IChunkRepository>();
        chunkRepository.Setup(r => r.ListByProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Chunk>)[chunkA, chunkB]);

        var memoryRepository = new Mock<IMemoryRepository>();
        memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Context.Domain.Entities.Memory>)[]);

        var embeddingRepository = new Mock<IEmbeddingRepository>();
        // A cache row already exists for "hash-a" under this provider - the handler must never even
        // consult this (let alone treat it as a reason to skip regenerating), so GetByContentHashAsync
        // is configured here only to prove that, not because the handler is expected to call it.
        embeddingRepository.Setup(e => e.GetByContentHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Context.Domain.Entities.Embedding
            {
                ContentHash = "hash-a",
                Provider = "fake:fake-model",
                Model = "fake-model",
                Dim = FixedVector.Length,
                Vector = Context.Application.Support.EmbeddingVectorCodec.Encode(FixedVector),
                CreatedAt = DateTimeOffset.UtcNow
            });

        var chunkVectorIndex = new Mock<IChunkVectorIndex>();
        var memoryStore = new Mock<IMemoryStore>();
        var resolver = new FakeProviderResolver(FixedVector);

        var sut = new RecreateEmbeddingsJobHandler(
            chunkRepository.Object, memoryRepository.Object, embeddingRepository.Object,
            chunkVectorIndex.Object, memoryStore.Object, resolver);

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        embeddingRepository.Verify(
            e => e.UpsertAsync(It.Is<Context.Domain.Entities.Embedding>(em => em.ContentHash == "hash-a"), It.IsAny<CancellationToken>()), Times.Once);
        embeddingRepository.Verify(
            e => e.UpsertAsync(It.Is<Context.Domain.Entities.Embedding>(em => em.ContentHash == "hash-b"), It.IsAny<CancellationToken>()), Times.Once);
        embeddingRepository.Verify(
            e => e.GetByContentHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_upserts_the_vector_index_once_per_chunk_id_even_when_two_chunks_share_one_content_hash()
    {
        var projectId = Guid.NewGuid();
        const string sharedHash = "shared-hash";
        var chunk1 = MakeChunk(contentHash: sharedHash, text: "identical text");
        var chunk2 = MakeChunk(contentHash: sharedHash, text: "identical text");

        var chunkRepository = new Mock<IChunkRepository>();
        chunkRepository.Setup(r => r.ListByProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Chunk>)[chunk1, chunk2]);

        var memoryRepository = new Mock<IMemoryRepository>();
        memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Context.Domain.Entities.Memory>)[]);

        var embeddingRepository = new Mock<IEmbeddingRepository>();
        var chunkVectorIndex = new Mock<IChunkVectorIndex>();
        var memoryStore = new Mock<IMemoryStore>();
        var resolver = new FakeProviderResolver(FixedVector);

        var sut = new RecreateEmbeddingsJobHandler(
            chunkRepository.Object, memoryRepository.Object, embeddingRepository.Object,
            chunkVectorIndex.Object, memoryStore.Object, resolver);

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        chunkVectorIndex.Verify(
            v => v.UpsertAsync(chunk1.Id, projectId, It.IsAny<string>(), It.Is<float[]>(vec => vec.SequenceEqual(FixedVector)), It.IsAny<CancellationToken>()),
            Times.Once);
        chunkVectorIndex.Verify(
            v => v.UpsertAsync(chunk2.Id, projectId, It.IsAny<string>(), It.Is<float[]>(vec => vec.SequenceEqual(FixedVector)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_re_embeds_live_knowledge_and_skips_events()
    {
        var projectId = Guid.NewGuid();
        var knowledge = MakeMemory(projectId, MemoryType.Decision);
        var sessionEvent = MakeMemory(projectId, MemoryType.Event);

        var chunkRepository = new Mock<IChunkRepository>();
        chunkRepository.Setup(r => r.ListByProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Chunk>)[]);

        var memoryRepository = new Mock<IMemoryRepository>();
        memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Context.Domain.Entities.Memory>)[knowledge, sessionEvent]);

        var memoryStore = new Mock<IMemoryStore>();
        var sut = new RecreateEmbeddingsJobHandler(
            chunkRepository.Object, memoryRepository.Object, new Mock<IEmbeddingRepository>().Object,
            new Mock<IChunkVectorIndex>().Object, memoryStore.Object, new FakeProviderResolver(FixedVector));

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        memoryStore.Verify(
            s => s.UpsertEmbeddingAsync(
                It.Is<Context.Domain.Entities.Memory>(m => m.Id == knowledge.Id),
                It.Is<MemoryVector>(v => v.ModelKey == "unknown" && v.Vector.SequenceEqual(FixedVector)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        memoryStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_embeds_chunks_and_memories_in_provider_sized_batches()
    {
        var projectId = Guid.NewGuid();
        var chunkRepository = new Mock<IChunkRepository>();
        chunkRepository.Setup(r => r.ListByProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Chunk>)[MakeChunk("h1", "one"), MakeChunk("h2", "two"), MakeChunk("h2", "two"), MakeChunk("h3", "three")]);
        var memoryRepository = new Mock<IMemoryRepository>();
        memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Context.Domain.Entities.Memory>)
                [MakeMemory(projectId, MemoryType.Decision), MakeMemory(projectId, MemoryType.Decision), MakeMemory(projectId, MemoryType.Decision)]);

        var generator = new FakeEmbeddingGenerator(FixedVector);
        var resolver = new Mock<IProviderResolver>();
        resolver.Setup(r => r.GetEmbeddingGeneratorAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchingEmbeddingGenerator(generator, new EmbeddingBatchLimits(2, int.MaxValue)));
        var embeddingRepository = new Mock<IEmbeddingRepository>();
        var memoryStore = new Mock<IMemoryStore>();

        var sut = new RecreateEmbeddingsJobHandler(
            chunkRepository.Object, memoryRepository.Object, embeddingRepository.Object,
            new Mock<IChunkVectorIndex>().Object, memoryStore.Object, resolver.Object);

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        generator.RequestSizes.Should().Equal(2, 1, 2, 1);
        embeddingRepository.Verify(e => e.UpsertAsync(It.IsAny<Context.Domain.Entities.Embedding>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        memoryStore.Verify(
            s => s.UpsertEmbeddingAsync(It.IsAny<Context.Domain.Entities.Memory>(), It.IsAny<MemoryVector>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    private static Job MakeJob(Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.RecreateEmbeddings,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(new RecreateEmbeddingsPayload(projectId)),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static Chunk MakeChunk(string contentHash, string text) => new()
    {
        Id = Guid.NewGuid(),
        FileId = Guid.NewGuid(),
        Text = text,
        TokenCount = text.Split(' ').Length,
        ContentHash = contentHash
    };

    private static Context.Domain.Entities.Memory MakeMemory(Guid projectId, MemoryType type) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = type,
        Title = "Use SQLite for local caching",
        Body = "The team decided to use SQLite for local caching due to zero-ops deployment.",
        CreatedAt = DateTimeOffset.UtcNow,
        LastReinforcedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Always resolves to the one fixed embedding generator it was constructed with,
    /// regardless of projectId; <see cref="GetChatClientAsync"/> is never expected to be called by
    /// this handler, so it throws if it ever is.</summary>
    private sealed class FakeProviderResolver(float[] vector) : IProviderResolver
    {
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = new FakeEmbeddingGenerator(vector);

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(_embeddingGenerator);

        public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException($"{nameof(RecreateEmbeddingsJobHandler)} never touches {nameof(IChatClient)}.");
    }

    /// <summary>Always returns <paramref name="vector"/>, regardless of the input text.</summary>
    private sealed class FakeEmbeddingGenerator(float[] vector) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<int> RequestSizes { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
        {
            var inputs = values.ToList();
            RequestSizes.Add(inputs.Count);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(vector))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
