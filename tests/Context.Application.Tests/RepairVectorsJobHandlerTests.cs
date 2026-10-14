using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

public sealed class RepairVectorsJobHandlerTests
{
    private const string Provider = "fake:embed";

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Mock<IChunkRepository> _chunks = new();
    private readonly Mock<IChunkVectorIndex> _chunkVectors = new();
    private readonly Mock<IMemoryRepository> _memories = new();
    private readonly Mock<IMemoryVectorIndex> _memoryVectors = new();
    private readonly Mock<ISessionRepository> _sessions = new();
    private readonly Mock<ISessionSummaryVectorIndex> _summaryVectors = new();
    private readonly Mock<IProviderResolver> _providers = new();

    public RepairVectorsJobHandlerTests()
    {
        _chunkVectors.Setup(v => v.ListChunkIdsAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid>());
        _chunks.Setup(c => c.ListIdsByProjectAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid>());
        _memoryVectors.Setup(v => v.ListMemoryIdsAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid>());
        _memories.Setup(m => m.ListByProjectAsync(_projectId, false, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<DomainMemory>)[]);
        _sessions.Setup(s => s.ListSummaryChunksByProjectAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<SessionSummaryChunk>)[]);
        _providers.Setup(p => p.GetEmbeddingGeneratorAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new FakeEmbeddingGenerator());
        _summaryVectors.Setup(v => v.ListChunkIdsAsync(_projectId, Provider, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid>());
    }

    private RepairVectorsJobHandler CreateSut() => new(
        _chunks.Object, _chunkVectors.Object, _memories.Object, _memoryVectors.Object, _sessions.Object, _summaryVectors.Object, _providers.Object);

    private Job MakeJob() => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.RepairVectors,
        ProjectId = _projectId,
        PayloadJson = JsonSerializer.Serialize(new RepairVectorsPayload(_projectId)),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static SessionSummaryChunk Summary(string? title) => new()
    {
        Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), Title = title, GeneratedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Chunk_vectors_without_a_chunk_row_are_removed_and_the_rest_kept()
    {
        var (kept, orphan) = (Guid.NewGuid(), Guid.NewGuid());
        _chunkVectors.Setup(v => v.ListChunkIdsAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { kept, orphan });
        _chunks.Setup(c => c.ListIdsByProjectAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { kept });

        await CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        _chunkVectors.Verify(v => v.DeleteAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == orphan), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Nothing_is_deleted_when_the_indexes_already_match_the_database()
    {
        var id = Guid.NewGuid();
        _chunkVectors.Setup(v => v.ListChunkIdsAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { id });
        _chunks.Setup(c => c.ListIdsByProjectAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { id });

        await CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        _chunkVectors.Verify(v => v.DeleteAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        _memoryVectors.Verify(v => v.DeleteAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Memory_vectors_of_memories_that_are_no_longer_live_are_removed()
    {
        var live = new DomainMemory { Id = Guid.NewGuid(), ProjectId = _projectId, Title = "t", Body = "b" };
        var gone = Guid.NewGuid();
        _memories.Setup(m => m.ListByProjectAsync(_projectId, false, It.IsAny<CancellationToken>())).ReturnsAsync([live]);
        _memoryVectors.Setup(v => v.ListMemoryIdsAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { live.Id, gone });

        await CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        _memoryVectors.Verify(v => v.DeleteAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == gone), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Session_summary_chunks_missing_a_vector_are_embedded_and_indexed_under_the_current_provider()
    {
        var indexed = Summary("Already there");
        var missing = Summary("Needs a vector");
        var blank = Summary(null);
        _sessions.Setup(s => s.ListSummaryChunksByProjectAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync([indexed, missing, blank]);
        _summaryVectors.Setup(v => v.ListChunkIdsAsync(_projectId, Provider, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { indexed.Id });

        await CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        _summaryVectors.Verify(
            v => v.UpsertAsync(missing.Id, _projectId, missing.SessionId, Provider, It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Once);
        _summaryVectors.Verify(
            v => v.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Once,
            "an already indexed chunk and a blank one need nothing");
    }

    [Fact]
    public async Task One_failing_step_does_not_stop_the_others_and_the_job_still_fails_so_it_is_retried()
    {
        var orphan = Guid.NewGuid();
        _chunkVectors.Setup(v => v.ListChunkIdsAsync(_projectId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("sqlite-vec is locked"));
        _memoryVectors.Setup(v => v.ListMemoryIdsAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<Guid> { orphan });
        var missing = Summary("Needs a vector");
        _sessions.Setup(s => s.ListSummaryChunksByProjectAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync([missing]);

        var act = () => CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        await act.Should().ThrowAsync<AggregateException>();
        _memoryVectors.Verify(v => v.DeleteAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
        _summaryVectors.Verify(
            v => v.UpsertAsync(missing.Id, _projectId, missing.SessionId, Provider, It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly EmbeddingGeneratorMetadata Metadata = new("fake", null, "embed");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f }))));

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }
}
