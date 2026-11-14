using Context.Graph.Tests.Fixtures;
using FluentAssertions;

namespace Context.Graph.Tests;

public class InMemoryGraphCacheTests
{
    [Fact]
    public async Task CreateAsync_HydratesFromPersistence()
    {
        var fixture = new GraphFixture();
        var persistence = fixture.SeedPersistence();

        var cache = await Context.Graph.InMemoryGraphCache.CreateAsync(fixture.ProjectId, persistence, CancellationToken.None);

        cache.ProjectId.Should().Be(fixture.ProjectId);
        cache.AllNodes().Should().HaveCount(fixture.AllNodes.Count);
        foreach (var node in fixture.AllNodes)
            cache.ContainsNode(node.Id).Should().BeTrue();
    }

    [Fact]
    public void InvalidateOwningFile_RemovesOnlyNodesOwnedByThatFile()
    {
        var fixture = new GraphFixture();
        var cache = new Context.Graph.InMemoryGraphCache(fixture.ProjectId);
        cache.Hydrate(fixture.AllNodes, fixture.AllEdges);

        cache.InvalidateOwningFile(fixture.ProjectId, fixture.FileCaller1);

        cache.ContainsNode(fixture.Caller1.Id).Should().BeFalse("Caller1 is owned by FileCaller1");
        cache.ContainsNode(fixture.Interface.Id).Should().BeTrue();
        cache.ContainsNode(fixture.Impl.Id).Should().BeTrue();
        cache.ContainsNode(fixture.Caller2.Id).Should().BeTrue();
        cache.ContainsNode(fixture.Test1.Id).Should().BeTrue();
        cache.ContainsNode(fixture.Unrelated.Id).Should().BeTrue();
    }

    [Fact]
    public void InvalidateOwningFile_AlsoRemovesDanglingEdgesEvenWhenOwnedByAnotherFile()
    {
        var fixture = new GraphFixture();
        var cache = new Context.Graph.InMemoryGraphCache(fixture.ProjectId);
        cache.Hydrate(fixture.AllNodes, fixture.AllEdges);

        // Caller1's own file (FileCaller1) owns CallsImplEdge, but Caller1 is also the TARGET of
        // CallsCaller1Edge (owned by FileCaller2) and TestsEdge (owned by FileTest1) - both must
        // be dropped too once Caller1 disappears, even though neither is owned by FileCaller1.
        cache.InvalidateOwningFile(fixture.ProjectId, fixture.FileCaller1);

        cache.GetOutgoing(fixture.Caller2.Id).Should().BeEmpty("Caller2 -> Caller1 is now dangling");
        cache.GetOutgoing(fixture.Test1.Id).Should().BeEmpty("Test1 -> Caller1 is now dangling");
        cache.GetIncoming(fixture.Impl.Id).Should().BeEmpty("Caller1 -> Impl is gone with Caller1's owning file");

        // Edges untouched by the invalidated file survive.
        cache.GetOutgoing(fixture.Impl.Id).Should().ContainSingle(e => e.Id == fixture.ImplementsEdge.Id);
        cache.GetIncoming(fixture.Interface.Id).Should().ContainSingle(e => e.Id == fixture.ImplementsEdge.Id);
    }

    [Fact]
    public void InvalidateOwningFile_UnknownFileId_LeavesCacheUnchanged()
    {
        var fixture = new GraphFixture();
        var cache = new Context.Graph.InMemoryGraphCache(fixture.ProjectId);
        cache.Hydrate(fixture.AllNodes, fixture.AllEdges);

        cache.InvalidateOwningFile(fixture.ProjectId, Guid.NewGuid());

        cache.AllNodes().Should().HaveCount(fixture.AllNodes.Count);
        foreach (var node in fixture.AllNodes)
            cache.ContainsNode(node.Id).Should().BeTrue();
    }

    [Fact]
    public void InvalidateOwningFile_WrongProjectId_Throws()
    {
        var fixture = new GraphFixture();
        var cache = new Context.Graph.InMemoryGraphCache(fixture.ProjectId);
        cache.Hydrate(fixture.AllNodes, fixture.AllEdges);

        var act = () => cache.InvalidateOwningFile(Guid.NewGuid(), fixture.FileCaller1);

        act.Should().Throw<ArgumentException>();
        cache.ContainsNode(fixture.Caller1.Id).Should().BeTrue("the mismatched-project call must be a no-op");
    }

    [Fact]
    public void Hydrate_ReAddingSameEdgeId_DoesNotDuplicateAdjacency()
    {
        var fixture = new GraphFixture();
        var cache = new Context.Graph.InMemoryGraphCache(fixture.ProjectId);
        cache.Hydrate(fixture.AllNodes, fixture.AllEdges);

        // Re-hydrating with the exact same edge (e.g. a re-index that re-upserts unchanged data)
        // must be idempotent, not append a second copy to the adjacency lists.
        cache.Hydrate(Array.Empty<Context.Domain.Entities.GraphNode>(), new[] { fixture.CallsImplEdge });

        cache.GetOutgoing(fixture.Caller1.Id).Should().ContainSingle(e => e.Id == fixture.CallsImplEdge.Id);
        cache.GetIncoming(fixture.Impl.Id).Should().ContainSingle(e => e.Id == fixture.CallsImplEdge.Id);
    }

    [Fact]
    public void InvalidateOwningFile_ThenHydrate_ReindexingUpdatesInPlace()
    {
        var fixture = new GraphFixture();
        var cache = new Context.Graph.InMemoryGraphCache(fixture.ProjectId);
        cache.Hydrate(fixture.AllNodes, fixture.AllEdges);

        cache.InvalidateOwningFile(fixture.ProjectId, fixture.FileCaller1);
        cache.ContainsNode(fixture.Caller1.Id).Should().BeFalse();

        // Simulate a re-index of that same file producing a fresh node/edge for it.
        var revised = new Context.Domain.Entities.GraphNode
        {
            Id = fixture.Caller1.Id, ProjectId = fixture.ProjectId, Kind = fixture.Caller1.Kind,
            Label = "OrderService.PayAsync", OwningFileId = fixture.FileCaller1,
        };
        cache.Hydrate(new[] { revised }, new[] { fixture.CallsImplEdge });

        cache.GetNode(fixture.Caller1.Id)!.Label.Should().Be("OrderService.PayAsync");
        cache.GetOutgoing(fixture.Caller1.Id).Should().ContainSingle(e => e.Id == fixture.CallsImplEdge.Id);
    }

    [Fact]
    public void ReplaceOwningFile_PreservesIncomingCrossFileEdgesWhileRemovingStaleNodesAndOwnedEdges()
    {
        var fixture = new GraphFixture();
        var staleNodeInCaller1File = new Context.Domain.Entities.GraphNode
        {
            Id = Guid.NewGuid(), ProjectId = fixture.ProjectId, Kind = Context.Domain.Enums.GraphNodeKind.Method,
            Label = "OrderService.OldHelper", OwningFileId = fixture.FileCaller1,
        };
        var staleEdgeInCaller1File = new Context.Domain.Entities.GraphEdge
        {
            Id = Guid.NewGuid(), ProjectId = fixture.ProjectId,
            SourceNodeId = fixture.Caller1.Id, TargetNodeId = staleNodeInCaller1File.Id,
            Kind = Context.Domain.Enums.GraphEdgeKind.Calls, OwningFileId = fixture.FileCaller1,
        };

        var cache = new Context.Graph.InMemoryGraphCache(fixture.ProjectId);
        cache.Hydrate([.. fixture.AllNodes, staleNodeInCaller1File], [.. fixture.AllEdges, staleEdgeInCaller1File]);

        // Updating FileCaller1 keeps Caller1, drops staleNodeInCaller1File & staleEdgeInCaller1File,
        // and MUST preserve Caller2 -> Caller1 and Test1 -> Caller1 incoming edges from other files.
        cache.ReplaceOwningFile(fixture.ProjectId, fixture.FileCaller1, [fixture.Caller1], [fixture.CallsImplEdge]);

        cache.ContainsNode(fixture.Caller1.Id).Should().BeTrue();
        cache.ContainsNode(staleNodeInCaller1File.Id).Should().BeFalse();
        cache.GetOutgoing(fixture.Caller1.Id).Should().ContainSingle(e => e.Id == fixture.CallsImplEdge.Id);
        cache.GetIncoming(fixture.Caller1.Id).Select(e => e.Id).Should().BeEquivalentTo(
            [fixture.CallsCaller1Edge.Id, fixture.TestsEdge.Id]);
    }
}
