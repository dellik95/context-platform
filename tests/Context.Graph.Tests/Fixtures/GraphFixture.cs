using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Tests.Fakes;

namespace Context.Graph.Tests.Fixtures;

/// <summary>
/// Hand-built graph shared by <see cref="Context.Graph.Tests.InMemoryGraphCacheTests"/> and
/// <see cref="Context.Graph.Tests.GraphTraversalTests"/>.
///
/// Shape (all edges point from the "calling"/"implementing"/"testing" side towards the thing
/// they act on, matching how <see cref="Context.Domain.Entities.SymbolRelation"/>/<see
/// cref="GraphEdge"/> are produced by the indexer):
///
///   Test1 --Tests--&gt;  Caller1
///   Caller2 --Calls--&gt; Caller1 --Calls--&gt; Impl --Implements--&gt; Interface
///   Unrelated (isolated node, no edges - negative-path fixture)
///
/// Each node lives in its own owning file (FileA..FileF) so invalidate-by-owning-file tests can
/// remove exactly one node (and its dangling edges) without touching the rest of the graph.
/// </summary>
internal sealed class GraphFixture
{
    public Guid ProjectId { get; } = Guid.NewGuid();

    public Guid FileInterface { get; } = Guid.NewGuid();
    public Guid FileImpl { get; } = Guid.NewGuid();
    public Guid FileCaller1 { get; } = Guid.NewGuid();
    public Guid FileCaller2 { get; } = Guid.NewGuid();
    public Guid FileTest1 { get; } = Guid.NewGuid();
    public Guid FileUnrelated { get; } = Guid.NewGuid();

    public GraphNode Interface { get; }
    public GraphNode Impl { get; }
    public GraphNode Caller1 { get; }
    public GraphNode Caller2 { get; }
    public GraphNode Test1 { get; }
    public GraphNode Unrelated { get; }

    public GraphEdge ImplementsEdge { get; }   // Impl -Implements-> Interface
    public GraphEdge CallsImplEdge { get; }    // Caller1 -Calls-> Impl
    public GraphEdge CallsCaller1Edge { get; } // Caller2 -Calls-> Caller1
    public GraphEdge TestsEdge { get; }        // Test1 -Tests-> Caller1

    public IReadOnlyList<GraphNode> AllNodes { get; }
    public IReadOnlyList<GraphEdge> AllEdges { get; }

    public GraphFixture()
    {
        Interface = new GraphNode
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId, Kind = GraphNodeKind.Interface,
            Label = "IPaymentGateway", OwningFileId = FileInterface,
        };
        Impl = new GraphNode
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId, Kind = GraphNodeKind.Method,
            Label = "StripeGateway.Charge", OwningFileId = FileImpl,
        };
        Caller1 = new GraphNode
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId, Kind = GraphNodeKind.Method,
            Label = "OrderService.Pay", OwningFileId = FileCaller1,
        };
        Caller2 = new GraphNode
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId, Kind = GraphNodeKind.Method,
            Label = "CheckoutController.Submit", OwningFileId = FileCaller2,
        };
        Test1 = new GraphNode
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId, Kind = GraphNodeKind.Test,
            Label = "OrderServiceTests.Test_Pay", OwningFileId = FileTest1,
        };
        Unrelated = new GraphNode
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId, Kind = GraphNodeKind.Function,
            Label = "Unrelated.Helper", OwningFileId = FileUnrelated,
        };

        ImplementsEdge = new GraphEdge
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId,
            SourceNodeId = Impl.Id, TargetNodeId = Interface.Id,
            Kind = GraphEdgeKind.Implements, OwningFileId = FileImpl,
        };
        CallsImplEdge = new GraphEdge
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId,
            SourceNodeId = Caller1.Id, TargetNodeId = Impl.Id,
            Kind = GraphEdgeKind.Calls, OwningFileId = FileCaller1,
        };
        CallsCaller1Edge = new GraphEdge
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId,
            SourceNodeId = Caller2.Id, TargetNodeId = Caller1.Id,
            Kind = GraphEdgeKind.Calls, OwningFileId = FileCaller2,
        };
        TestsEdge = new GraphEdge
        {
            Id = Guid.NewGuid(), ProjectId = ProjectId,
            SourceNodeId = Test1.Id, TargetNodeId = Caller1.Id,
            Kind = GraphEdgeKind.Tests, OwningFileId = FileTest1,
        };

        AllNodes = new[] { Interface, Impl, Caller1, Caller2, Test1, Unrelated };
        AllEdges = new[] { ImplementsEdge, CallsImplEdge, CallsCaller1Edge, TestsEdge };
    }

    /// <summary>A <see cref="FakeGraphPersistence"/> pre-seeded with this fixture's full graph.</summary>
    public FakeGraphPersistence SeedPersistence() => new FakeGraphPersistence().Seed(AllNodes, AllEdges);
}
