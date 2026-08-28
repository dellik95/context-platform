using Context.Domain.Enums;
using Context.Graph.Tests.Fixtures;
using FluentAssertions;

namespace Context.Graph.Tests;

public class GraphTraversalTests
{
    private static async Task<(GraphFixture Fixture, Context.Graph.GraphTraversal Traversal)> BuildAsync()
    {
        var fixture = new GraphFixture();
        var persistence = fixture.SeedPersistence();
        var cacheProvider = new Context.Graph.GraphCacheProvider(persistence);

        // Warm the cache up-front - GraphTraversal resolves a node's project purely by scanning
        // already-loaded caches (IGraphCacheProvider.FindCacheContaining), so a cold cache would
        // make every query below return empty/null regardless of the seeded data.
        await cacheProvider.GetOrCreateAsync(fixture.ProjectId, CancellationToken.None);

        return (fixture, new Context.Graph.GraphTraversal(cacheProvider));
    }

    // ---- NeighborsAsync ------------------------------------------------

    [Fact]
    public async Task NeighborsAsync_OneHop_ReturnsBothIncomingAndOutgoingNeighbors()
    {
        var (fixture, traversal) = await BuildAsync();

        var neighbors = await traversal.NeighborsAsync(fixture.Caller1.Id, kinds: null, hops: 1, CancellationToken.None);

        neighbors.Select(n => n.Id).Should().BeEquivalentTo(new[]
        {
            fixture.Impl.Id,    // Caller1 -Calls-> Impl (outgoing)
            fixture.Caller2.Id, // Caller2 -Calls-> Caller1 (incoming)
            fixture.Test1.Id,   // Test1 -Tests-> Caller1 (incoming)
        });
    }

    [Fact]
    public async Task NeighborsAsync_TwoHops_ExpandsFurtherAlongTheChain()
    {
        var (fixture, traversal) = await BuildAsync();

        var neighbors = await traversal.NeighborsAsync(fixture.Caller1.Id, kinds: null, hops: 2, CancellationToken.None);

        neighbors.Select(n => n.Id).Should().BeEquivalentTo(new[]
        {
            fixture.Impl.Id, fixture.Caller2.Id, fixture.Test1.Id, fixture.Interface.Id,
        });
    }

    [Fact]
    public async Task NeighborsAsync_KindFilter_ExcludesNonMatchingEdges()
    {
        var (fixture, traversal) = await BuildAsync();

        var neighbors = await traversal.NeighborsAsync(
            fixture.Interface.Id, kinds: new[] { GraphEdgeKind.Implements }, hops: 5, CancellationToken.None);

        // Impl -Implements-> Interface is the only Implements edge in the fixture; walking beyond
        // Impl would require a Calls edge, which the filter excludes even though hops allows it.
        neighbors.Select(n => n.Id).Should().BeEquivalentTo(new[] { fixture.Impl.Id });
    }

    [Fact]
    public async Task NeighborsAsync_ZeroHops_ReturnsEmpty()
    {
        var (fixture, traversal) = await BuildAsync();

        var neighbors = await traversal.NeighborsAsync(fixture.Caller1.Id, kinds: null, hops: 0, CancellationToken.None);

        neighbors.Should().BeEmpty();
    }

    [Fact]
    public async Task NeighborsAsync_UnknownNode_ReturnsEmpty()
    {
        var (_, traversal) = await BuildAsync();

        var neighbors = await traversal.NeighborsAsync(Guid.NewGuid(), kinds: null, hops: 3, CancellationToken.None);

        neighbors.Should().BeEmpty();
    }

    // ---- ShortestPathAsync ----------------------------------------------

    [Fact]
    public async Task ShortestPathAsync_FindsShortestUndirectedPathAcrossMixedEdgeKinds()
    {
        var (fixture, traversal) = await BuildAsync();

        var path = await traversal.ShortestPathAsync(fixture.Caller2.Id, fixture.Interface.Id, CancellationToken.None);

        path.Should().NotBeNull();
        path!.Select(n => n.Id).Should().Equal(
            fixture.Caller2.Id, fixture.Caller1.Id, fixture.Impl.Id, fixture.Interface.Id);
    }

    [Fact]
    public async Task ShortestPathAsync_SameStartAndEnd_ReturnsSingleNode()
    {
        var (fixture, traversal) = await BuildAsync();

        var path = await traversal.ShortestPathAsync(fixture.Caller1.Id, fixture.Caller1.Id, CancellationToken.None);

        path.Should().NotBeNull();
        path!.Select(n => n.Id).Should().Equal(fixture.Caller1.Id);
    }

    [Fact]
    public async Task ShortestPathAsync_NoPathExists_ReturnsNull()
    {
        var (fixture, traversal) = await BuildAsync();

        var path = await traversal.ShortestPathAsync(fixture.Caller1.Id, fixture.Unrelated.Id, CancellationToken.None);

        path.Should().BeNull();
    }

    [Fact]
    public async Task ShortestPathAsync_UnknownTargetNode_ReturnsNull()
    {
        var (fixture, traversal) = await BuildAsync();

        var path = await traversal.ShortestPathAsync(fixture.Caller1.Id, Guid.NewGuid(), CancellationToken.None);

        path.Should().BeNull();
    }

    // ---- BlastRadiusAsync -------------------------------------------------

    [Fact]
    public async Task BlastRadiusAsync_SeparatesCallersImplementersAndCoveringTests()
    {
        var (fixture, traversal) = await BuildAsync();

        var impact = await traversal.BlastRadiusAsync(fixture.Interface.Id, maxHops: 2, CancellationToken.None);

        // Callers: reverse-walks Calls/References/Implements/Extends from Interface up to 2 hops.
        //   hop1: Impl (-Implements->), hop2: Caller1 (-Calls-> Impl). Caller2 is hop3 - out of range.
        impact.Callers.Should().HaveCount(2);
        impact.Callers.Should().ContainSingle(i => i.Node.Id == fixture.Impl.Id && i.Hops == 1);
        impact.Callers.Should().ContainSingle(i => i.Node.Id == fixture.Caller1.Id && i.Hops == 2);

        // Implementers: same reverse walk restricted to Implements/Extends - only Impl qualifies,
        // since Caller1 reaches Impl via a Calls edge, not Implements/Extends.
        impact.Implementers.Should().ContainSingle();
        impact.Implementers[0].Node.Id.Should().Be(fixture.Impl.Id);
        impact.Implementers[0].Hops.Should().Be(1);

        // Covering tests: Test1 tests Caller1, and Caller1 is a (transitive) caller of Interface,
        // so Test1 counts as covering the blast radius even though it never touches Interface itself.
        impact.CoveringTests.Should().ContainSingle();
        impact.CoveringTests[0].Node.Id.Should().Be(fixture.Test1.Id);
    }

    [Fact]
    public async Task BlastRadiusAsync_MaxHopsOne_ExcludesTransitiveCallerAndItsTest()
    {
        var (fixture, traversal) = await BuildAsync();

        var impact = await traversal.BlastRadiusAsync(fixture.Interface.Id, maxHops: 1, CancellationToken.None);

        impact.Callers.Should().ContainSingle(i => i.Node.Id == fixture.Impl.Id);
        impact.Implementers.Should().ContainSingle(i => i.Node.Id == fixture.Impl.Id);
        // Caller1 (hop2) is out of range at maxHops=1, so the test that only covers Caller1 -
        // not Interface or Impl directly - must not show up either.
        impact.CoveringTests.Should().BeEmpty();
    }

    [Fact]
    public async Task BlastRadiusAsync_LeafNodeWithNoDependents_ReturnsAllEmpty()
    {
        var (fixture, traversal) = await BuildAsync();

        var impact = await traversal.BlastRadiusAsync(fixture.Unrelated.Id, maxHops: 3, CancellationToken.None);

        impact.Callers.Should().BeEmpty();
        impact.Implementers.Should().BeEmpty();
        impact.CoveringTests.Should().BeEmpty();
    }

    [Fact]
    public async Task BlastRadiusAsync_UnknownNode_ReturnsAllEmptyWithoutThrowing()
    {
        var (_, traversal) = await BuildAsync();

        var impact = await traversal.BlastRadiusAsync(Guid.NewGuid(), maxHops: 3, CancellationToken.None);

        impact.Callers.Should().BeEmpty();
        impact.Implementers.Should().BeEmpty();
        impact.CoveringTests.Should().BeEmpty();
    }
}
