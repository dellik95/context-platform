using Context.Application.Abstractions;
using Context.Application.Adapters;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Moq;

namespace Context.Application.Tests;

/// <summary>
/// Covers the decorator that keeps the sqlite-vec store in step with chunk deletes. The vector table
/// has no foreign key back to <c>Chunks</c>, so it is the only thing standing between a re-index and
/// a permanently growing set of orphaned vectors.
/// </summary>
public sealed class VectorPruningRepositoryTests
{
    [Fact]
    public async Task ReplaceForFileAsync_prunes_only_the_chunk_ids_that_were_not_re_inserted()
    {
        var fileId = Guid.NewGuid();
        var reused = Guid.NewGuid();
        var dropped = Guid.NewGuid();

        var inner = new Mock<IChunkRepository>();
        inner.Setup(r => r.ReplaceForFileAsync(fileId, It.IsAny<IReadOnlyList<Chunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid>)[reused, dropped]);

        var vectorIndex = new Mock<IChunkVectorIndex>();
        var sut = new VectorPruningChunkRepository(inner.Object, vectorIndex.Object);

        // The re-index reuses `reused`'s id because its content hash is unchanged; pruning it would
        // discard a vector the very same call just re-inserted a row for.
        var removed = await sut.ReplaceForFileAsync(fileId, [new Chunk { Id = reused, Text = "chunk", ContentHash = "hash" }], CancellationToken.None);

        removed.Should().BeEquivalentTo([reused, dropped]);
        vectorIndex.Verify(v => v.DeleteAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == dropped), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReplaceForFileAsync_does_not_touch_the_vector_index_when_nothing_was_removed()
    {
        var inner = new Mock<IChunkRepository>();
        inner.Setup(r => r.ReplaceForFileAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<Chunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid>)[]);

        var vectorIndex = new Mock<IChunkVectorIndex>();
        var sut = new VectorPruningChunkRepository(inner.Object, vectorIndex.Object);

        await sut.ReplaceForFileAsync(Guid.NewGuid(), [new Chunk { Id = Guid.NewGuid(), Text = "chunk", ContentHash = "hash" }], CancellationToken.None);

        vectorIndex.Verify(v => v.DeleteAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
