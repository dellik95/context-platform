using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Application.Support;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

namespace Context.Application.Tests;

public sealed class EmbedChunkJobHandlerTests
{
    private const string ProviderKey = "fake:fake-model";

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Mock<IEmbeddingRepository> _embeddings = new();
    private readonly Mock<IChunkVectorIndex> _vectorIndex = new();
    private readonly RecordingEmbeddingGenerator _provider = new();
    private readonly List<Context.Domain.Entities.Embedding> _stored = [];

    public EmbedChunkJobHandlerTests()
    {
        _embeddings
            .Setup(e => e.GetByContentHashesAsync(It.IsAny<IEnumerable<string>>(), ProviderKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, Context.Domain.Entities.Embedding>());
        _embeddings
            .Setup(e => e.UpsertAsync(It.IsAny<Context.Domain.Entities.Embedding>(), It.IsAny<CancellationToken>()))
            .Callback<Context.Domain.Entities.Embedding, CancellationToken>((embedding, _) => _stored.Add(embedding))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task HandleAsync_sends_one_request_per_provider_batch_and_indexes_every_chunk()
    {
        var shared = Item("shared", chunks: 2);
        EmbedChunkItem[] items = [Item("a"), Item("b"), shared, Item("c"), Item("d")];

        await CreateSut(maxInputs: 2).HandleAsync(Job(items), CancellationToken.None);

        ShouldHaveRequested(["text a", "text b"], ["text shared", "text c"], ["text d"]);
        _stored.Select(e => e.ContentHash).Should().Equal("a", "b", "shared", "c", "d");
        _stored.Should().OnlyContain(e => e.Provider == ProviderKey && e.Model == "fake-model");
        foreach (var item in items)
        {
            foreach (var chunkId in item.ChunkIds)
            {
                _vectorIndex.Verify(v => v.UpsertAsync(
                    chunkId, _projectId, ProviderKey, It.Is<float[]>(vec => vec.SequenceEqual(VectorFor(item.Text))), It.IsAny<CancellationToken>()), Times.Once);
            }
        }
    }

    [Fact]
    public async Task HandleAsync_reuses_content_already_embedded_for_the_provider()
    {
        var cached = Item("cached");
        float[] cachedVector = [9f, 9f];
        _embeddings
            .Setup(e => e.GetByContentHashesAsync(It.IsAny<IEnumerable<string>>(), ProviderKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, Context.Domain.Entities.Embedding>
            {
                ["cached"] = new()
                {
                    ContentHash = "cached", Provider = ProviderKey, Model = "fake-model", Dim = 2,
                    Vector = EmbeddingVectorCodec.Encode(cachedVector), CreatedAt = DateTimeOffset.UtcNow,
                },
            });

        await CreateSut(maxInputs: 16).HandleAsync(Job(Item("a"), cached, Item("b")), CancellationToken.None);

        ShouldHaveRequested(["text a", "text b"]);
        _stored.Select(e => e.ContentHash).Should().Equal("a", "b");
        _vectorIndex.Verify(v => v.UpsertAsync(
            cached.ChunkIds[0], _projectId, ProviderKey, It.Is<float[]>(vec => vec.SequenceEqual(cachedVector)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_retries_a_batch_rejected_for_context_length_item_by_item_and_fails_naming_the_rest()
    {
        var tooLong = Item("long", text: "TOO LONG");

        var act = () => CreateSut(maxInputs: 16).HandleAsync(Job(Item("a"), tooLong, Item("b")), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.WithMessage($"1 of 3 item(s) exceed*long (chunks {tooLong.ChunkIds[0]})*");
        thrown.WithInnerException<EmbeddingContextLengthException>();
        ShouldHaveRequested(["text a", "TOO LONG", "text b"], ["text a"], ["TOO LONG"], ["text b"]);
        _stored.Select(e => e.ContentHash).Should().Equal("a", "b");
    }

    [Fact]
    public async Task HandleAsync_lets_other_provider_failures_fail_the_job_without_retrying_items()
    {
        _provider.Failure = new HttpRequestException("connection refused");

        var act = () => CreateSut(maxInputs: 16).HandleAsync(Job(Item("a"), Item("b")), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _provider.Requests.Should().ContainSingle();
        _stored.Should().BeEmpty();
    }

    private void ShouldHaveRequested(params string[][] requests) =>
        _provider.Requests.Should().BeEquivalentTo(requests, o => o.WithStrictOrdering());

    private EmbedChunkJobHandler CreateSut(int maxInputs)
    {
        var generator = new BatchingEmbeddingGenerator(_provider, new EmbeddingBatchLimits(maxInputs, int.MaxValue));
        var resolver = new Mock<IProviderResolver>();
        resolver.Setup(r => r.GetEmbeddingGeneratorAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(generator);
        return new EmbedChunkJobHandler(_embeddings.Object, _vectorIndex.Object, resolver.Object);
    }

    private Job Job(params EmbedChunkItem[] items) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.EmbedChunk,
        ProjectId = _projectId,
        PayloadJson = JsonSerializer.Serialize(new EmbedChunkPayload(_projectId, items)),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static EmbedChunkItem Item(string hash, int chunks = 1, string? text = null) =>
        new(hash, text ?? $"text {hash}", [.. Enumerable.Range(0, chunks).Select(_ => Guid.NewGuid())]);

    private static float[] VectorFor(string text) => [text.Length, text[^1]];

    /// <summary>Records every request; rejects any request containing "TOO LONG" the way Ollama's decorator does.</summary>
    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly EmbeddingGeneratorMetadata _metadata = new("fake", null, "fake-model");

        public List<string[]> Requests { get; } = [];

        public Exception? Failure { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var inputs = values.ToArray();
            Requests.Add(inputs);
            if (Failure is not null)
            {
                throw Failure;
            }

            if (inputs.Contains("TOO LONG"))
            {
                throw new EmbeddingContextLengthException("exceeds the context window", new InvalidOperationException("context length"));
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(t => new Embedding<float>(VectorFor(t)))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : null;

        public void Dispose()
        {
        }
    }
}
