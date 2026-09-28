using Context.Application.Abstractions;
using Context.Application.Adapters;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Moq;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

/// <summary>
/// Covers the two decorators that keep the sqlite-vec store in step with row deletes. The vector
/// tables have no foreign key back to <c>Chunks</c>/<c>Memories</c>, so these decorators are the
/// only thing standing between a re-index and a permanently growing set of orphaned vectors.
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

    [Fact]
    public async Task DeleteAsync_prunes_the_memorys_vector_after_the_row_is_gone()
    {
        var memoryId = Guid.NewGuid();
        var order = new List<string>();

        var inner = new Mock<IMemoryRepository>();
        inner.Setup(r => r.DeleteAsync(memoryId, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("row"))
            .Returns(Task.CompletedTask);

        var vectorIndex = new Mock<IMemoryVectorIndex>();
        vectorIndex.Setup(v => v.DeleteAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("vector"))
            .Returns(Task.CompletedTask);

        var sut = new VectorPruningMemoryRepository(inner.Object, vectorIndex.Object);

        await sut.DeleteAsync(memoryId, CancellationToken.None);

        // Row first: a failed row delete must leave the vector in place, not strand a searchable
        // memory with no way back into the index.
        order.Should().Equal("row", "vector");
        vectorIndex.Verify(v => v.DeleteAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == memoryId), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_leaves_the_vector_in_place_when_the_row_delete_throws()
    {
        var inner = new Mock<IMemoryRepository>();
        inner.Setup(r => r.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("row delete failed"));

        var vectorIndex = new Mock<IMemoryVectorIndex>();
        var sut = new VectorPruningMemoryRepository(inner.Object, vectorIndex.Object);

        await sut.Invoking(s => s.DeleteAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        vectorIndex.Verify(v => v.DeleteAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Memory_reads_and_writes_other_than_delete_pass_straight_through()
    {
        var memory = new DomainMemory { Id = Guid.NewGuid(), Title = "t", Body = "b" };
        var inner = new Mock<IMemoryRepository>();
        inner.Setup(r => r.AddAsync(memory, It.IsAny<CancellationToken>())).ReturnsAsync(memory);

        var vectorIndex = new Mock<IMemoryVectorIndex>();
        var sut = new VectorPruningMemoryRepository(inner.Object, vectorIndex.Object);

        (await sut.AddAsync(memory, CancellationToken.None)).Should().BeSameAs(memory);
        vectorIndex.VerifyNoOtherCalls();
    }
}
