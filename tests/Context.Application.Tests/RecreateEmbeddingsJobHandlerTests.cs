using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Context.Application.Tests;

/// <summary>
/// Tests for <see cref="RecreateEmbeddingsJobHandler"/>'s three defining behaviors: it never gates
/// on an existing <c>(ContentHash, Provider)</c> cache row (that's the whole point of a *force*
/// regeneration job — see <see cref="EmbedChunkJobHandler"/> for the handler that DOES cache-gate),
/// every chunk id sharing one content hash gets its own vector-index upsert, and every currently
/// active memory gets re-embedded and re-saved.
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
        var memoryStore = new FakeMemoryStore();
        var resolver = new FakeProviderResolver(FixedVector);

        var sut = new RecreateEmbeddingsJobHandler(
            chunkRepository.Object, memoryRepository.Object, embeddingRepository.Object,
            chunkVectorIndex.Object, memoryStore, resolver);

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
        var memoryStore = new FakeMemoryStore();
        var resolver = new FakeProviderResolver(FixedVector);

        var sut = new RecreateEmbeddingsJobHandler(
            chunkRepository.Object, memoryRepository.Object, embeddingRepository.Object,
            chunkVectorIndex.Object, memoryStore, resolver);

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        chunkVectorIndex.Verify(
            v => v.UpsertAsync(chunk1.Id, projectId, It.IsAny<string>(), It.Is<float[]>(vec => vec.SequenceEqual(FixedVector)), It.IsAny<CancellationToken>()),
            Times.Once);
        chunkVectorIndex.Verify(
            v => v.UpsertAsync(chunk2.Id, projectId, It.IsAny<string>(), It.Is<float[]>(vec => vec.SequenceEqual(FixedVector)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_re_embeds_and_saves_every_active_memory_returned_by_ListByProjectAsync()
    {
        var projectId = Guid.NewGuid();
        var memory = MakeMemory(projectId);

        var chunkRepository = new Mock<IChunkRepository>();
        chunkRepository.Setup(r => r.ListByProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Chunk>)[]);

        var memoryRepository = new Mock<IMemoryRepository>();
        memoryRepository.Setup(r => r.ListByProjectAsync(projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Context.Domain.Entities.Memory>)[memory]);

        var embeddingRepository = new Mock<IEmbeddingRepository>();
        var chunkVectorIndex = new Mock<IChunkVectorIndex>();
        var memoryStore = new FakeMemoryStore();
        var resolver = new FakeProviderResolver(FixedVector);

        var sut = new RecreateEmbeddingsJobHandler(
            chunkRepository.Object, memoryRepository.Object, embeddingRepository.Object,
            chunkVectorIndex.Object, memoryStore, resolver);

        await sut.HandleAsync(MakeJob(projectId), CancellationToken.None);

        memoryStore.Saved.Should().ContainSingle();
        var (savedMemory, savedEmbedding) = memoryStore.Saved.Single();
        savedMemory.Id.Should().Be(memory.Id);
        savedEmbedding.Should().NotBeNull();
        savedEmbedding!.Should().BeEquivalentTo(FixedVector);
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

    private static Context.Domain.Entities.Memory MakeMemory(Guid projectId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = MemoryType.Decision,
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
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(vector))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>In-memory <see cref="IMemoryStore"/> that just accumulates every <see cref="SaveAsync"/>
    /// call so a test can assert on what was (re-)saved -- <see cref="GetExistingWithEmbeddingsAsync"/>/
    /// <see cref="MarkSupersededAsync"/> are never called by <see cref="RecreateEmbeddingsJobHandler"/>
    /// (it reads memories via <see cref="IMemoryRepository.ListByProjectAsync"/> directly), so they're
    /// implemented only to satisfy the interface.</summary>
    private sealed class FakeMemoryStore : IMemoryStore
    {
        public List<(Context.Domain.Entities.Memory Memory, float[]? Embedding)> Saved { get; } = [];

        public Task<IReadOnlyList<ExistingMemoryWithEmbedding>> GetExistingWithEmbeddingsAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExistingMemoryWithEmbedding>>([]);

        public Task SaveAsync(Context.Domain.Entities.Memory memory, float[]? embedding, CancellationToken ct)
        {
            Saved.Add((memory, embedding));
            return Task.CompletedTask;
        }

        public Task MarkSupersededAsync(Guid oldMemoryId, Guid newMemoryId, CancellationToken ct) => Task.CompletedTask;
    }
}
