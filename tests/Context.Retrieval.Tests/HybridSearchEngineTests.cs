using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using FluentAssertions;

namespace Context.Retrieval.Tests;

public sealed class HybridSearchEngineTests
{
    [Fact]
    public async Task SearchAsync_semantic_boost_prioritizes_strong_vector_match()
    {
        var lexicalNode = Guid.NewGuid();
        var vectorNode = Guid.NewGuid();
        var searchIndex = new FakeSearchIndex(
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, (SymbolKind?)null, "lexical-snippet", 10.0), (Guid.NewGuid(), (Guid?)null, vectorNode, (SymbolKind?)null, "vector-snippet", 1.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, lexicalNode, (SymbolKind?)null, "lexical-snippet", 0.0), (Guid.NewGuid(), (Guid?)null, vectorNode, (SymbolKind?)null, "vector-snippet", 0.9)]);

        var scorer = new HybridScorer(new HybridScoringOptions
        {
            LexicalWeight = 0.5,
            VectorWeight = 0.5,
            GraphWeight = 0,
            RecencyWeight = 0,
            MemoryWeight = 0
        });

        var sut = new HybridSearchEngine(
            searchIndex,
            new FakeEmbeddingLookup(),
            new FakeGraphTraversal(),
            new FakeReranker(),
            scorer,
            new HybridSearchOptions
            {
                RerankTopK = 1,
                SemanticBoostEnabled = true,
                SemanticStrongMatchThreshold = 0.8,
                SemanticBoostFactor = 0.2
            });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", null, 1024), CancellationToken.None);

        hits.Should().HaveCount(2);
        hits[0].NodeId.Should().Be(vectorNode);
    }

    [Fact]
    public async Task SearchAsync_without_semantic_boost_keeps_pre_boost_order_for_tie()
    {
        var lexicalNode = Guid.NewGuid();
        var vectorNode = Guid.NewGuid();
        var searchIndex = new FakeSearchIndex(
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, (SymbolKind?)null, "lexical-snippet", 10.0), (Guid.NewGuid(), (Guid?)null, vectorNode, (SymbolKind?)null, "vector-snippet", 1.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, lexicalNode, (SymbolKind?)null, "lexical-snippet", 0.0), (Guid.NewGuid(), (Guid?)null, vectorNode, (SymbolKind?)null, "vector-snippet", 0.9)]);

        var scorer = new HybridScorer(new HybridScoringOptions
        {
            LexicalWeight = 0.5,
            VectorWeight = 0.5,
            GraphWeight = 0,
            RecencyWeight = 0,
            MemoryWeight = 0
        });

        var sut = new HybridSearchEngine(
            searchIndex,
            new FakeEmbeddingLookup(),
            new FakeGraphTraversal(),
            new FakeReranker(),
            scorer,
            new HybridSearchOptions
            {
                RerankTopK = 1,
                SemanticBoostEnabled = false,
                SemanticStrongMatchThreshold = 0.8,
                SemanticBoostFactor = 0.2
            });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", null, 1024), CancellationToken.None);

        hits.Should().HaveCount(2);
        hits[0].NodeId.Should().Be(lexicalNode);
    }

    [Fact]
    public async Task SearchAsync_falls_back_to_lexical_only_when_embedding_lookup_throws()
    {
        var lexicalNode = Guid.NewGuid();
        var searchIndex = new FakeSearchIndex(
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, (SymbolKind?)null, "lexical-snippet", 10.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, Guid.NewGuid(), (SymbolKind?)null, "unreachable-vector-snippet", 0.9)]);

        var scorer = new HybridScorer(new HybridScoringOptions
        {
            LexicalWeight = 0.5,
            VectorWeight = 0.5,
            GraphWeight = 0,
            RecencyWeight = 0,
            MemoryWeight = 0
        });

        var sut = new HybridSearchEngine(
            searchIndex,
            new ThrowingEmbeddingLookup(),
            new FakeGraphTraversal(),
            new FakeReranker(),
            scorer,
            new HybridSearchOptions { RerankTopK = 1 });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", null, 1024), CancellationToken.None);

        hits.Should().ContainSingle();
        hits[0].NodeId.Should().Be(lexicalNode);
    }

    [Fact]
    public async Task SearchAsync_drops_a_vector_only_candidate_whose_cosine_is_below_the_relevance_floor()
    {
        var lexicalNode = Guid.NewGuid();
        var distantNode = Guid.NewGuid();
        var searchIndex = new FakeSearchIndex(
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, (SymbolKind?)null, "lexical-snippet", 3.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, distantNode, (SymbolKind?)null, "distant-snippet", 0.11)]);

        var sut = NewEngine(searchIndex, new HybridSearchOptions { RerankTopK = 1, VectorRelevanceFloor = 0.25 });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", null, 1024), CancellationToken.None);

        hits.Should().ContainSingle().Which.NodeId.Should().Be(lexicalNode,
            "a 0.11 cosine is noise the ANN pass had to return, and min-max normalization would otherwise promote it to a perfect 1.0 vector score");
    }

    [Fact]
    public async Task SearchAsync_does_not_apply_the_semantic_boost_to_the_best_of_a_weak_batch()
    {
        var strongLexical = Guid.NewGuid();
        var bestOfWeakVectors = Guid.NewGuid();
        var searchIndex = new FakeSearchIndex(
            lexical: [(Guid.NewGuid(), (Guid?)null, strongLexical, (SymbolKind?)null, "lexical-snippet", 10.0), (Guid.NewGuid(), (Guid?)null, bestOfWeakVectors, (SymbolKind?)null, "weak-snippet", 9.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, bestOfWeakVectors, (SymbolKind?)null, "weak-snippet", 0.30), (Guid.NewGuid(), (Guid?)null, strongLexical, (SymbolKind?)null, "lexical-snippet", 0.26)]);

        var scorer = new HybridScorer(new HybridScoringOptions
        {
            LexicalWeight = 0,
            VectorWeight = 0.5,
            GraphWeight = 0,
            RecencyWeight = 0,
            MemoryWeight = 0
        });

        var sut = new HybridSearchEngine(
            searchIndex,
            new FakeEmbeddingLookup(),
            new FakeGraphTraversal(),
            new FakeReranker(),
            scorer,
            new HybridSearchOptions
            {
                RerankTopK = 1,
                SemanticBoostEnabled = true,
                SemanticStrongMatchThreshold = 0.8,
                SemanticBoostFactor = 0.5
            });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", null, 1024), CancellationToken.None);

        hits.Single(h => h.NodeId == bestOfWeakVectors).Score.Should().BeApproximately(0.5, 1e-9,
            "0.30 is not a strong semantic match; it only normalizes to 1.0 because it is the best of two weak ones, so no boost is owed");
    }

    [Fact]
    public async Task SearchAsync_gives_graph_credit_to_a_neighbor_but_not_to_the_seed_that_found_it()
    {
        var seedNode = Guid.NewGuid();
        var neighborNode = Guid.NewGuid();
        var searchIndex = new FakeSearchIndex(
            lexical: [(Guid.NewGuid(), (Guid?)null, seedNode, (SymbolKind?)null, "seed-snippet", 5.0)],
            vector: []);

        var graphOnlyScorer = new HybridScorer(new HybridScoringOptions
        {
            LexicalWeight = 0,
            VectorWeight = 0,
            GraphWeight = 1.0,
            RecencyWeight = 0,
            MemoryWeight = 0
        });

        var sut = new HybridSearchEngine(
            searchIndex,
            new FakeEmbeddingLookup(),
            new SingleNeighborGraphTraversal(neighborNode),
            new FakeReranker(),
            graphOnlyScorer,
            new HybridSearchOptions { RerankTopK = 1, SemanticBoostEnabled = false });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", null, 1024), CancellationToken.None);

        hits.Single(h => h.NodeId == neighborNode).Score.Should().BeApproximately(0.5, 1e-9);
        hits.Single(h => h.NodeId == seedNode).Score.Should().Be(0.0,
            "the seed was picked for already ranking top on lexical+vector, so graph proximity to itself is the same evidence counted twice");
    }

    [Fact]
    public async Task SearchAsync_skips_graph_expansion_for_a_scoped_query()
    {
        var seedNode = Guid.NewGuid();
        var neighborNode = Guid.NewGuid();
        var searchIndex = new FakeSearchIndex(
            lexical: [(Guid.NewGuid(), (Guid?)null, seedNode, (SymbolKind?)null, "seed-snippet", 5.0)],
            vector: []);

        var sut = new HybridSearchEngine(
            searchIndex,
            new FakeEmbeddingLookup(),
            new SingleNeighborGraphTraversal(neighborNode),
            new FakeReranker(),
            new HybridScorer(),
            new HybridSearchOptions { RerankTopK = 1 });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", ["src/Auth"], 1024), CancellationToken.None);

        hits.Should().ContainSingle().Which.NodeId.Should().Be(seedNode,
            "a graph neighbor carries no path, so it cannot be shown to satisfy the scope the caller asked for");
    }

    private static HybridSearchEngine NewEngine(ISearchIndex searchIndex, HybridSearchOptions options) =>
        new(searchIndex,
            new FakeEmbeddingLookup(),
            new FakeGraphTraversal(),
            new FakeReranker(),
            new HybridScorer(),
            options);

    private sealed class SingleNeighborGraphTraversal(Guid neighborId) : IGraphTraversal
    {
        public Task<IReadOnlyList<GraphNode>> NeighborsAsync(Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphNode>>(
                [new GraphNode { Id = neighborId, Kind = GraphNodeKind.Method, RefId = neighborId, Label = "Neighbor" }]);

        public Task<GraphNeighborhood> NeighborhoodAsync(Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct) =>
            Task.FromResult(new GraphNeighborhood([], []));

        public Task<GraphNeighborhood> FullGraphAsync(Guid projectId, GraphEdgeKind[]? kinds, CancellationToken ct) =>
            Task.FromResult(new GraphNeighborhood([], []));

        public Task<IReadOnlyList<GraphNode>?> ShortestPathAsync(Guid fromId, Guid toId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphNode>?>([]);

        public Task<ImpactSet> BlastRadiusAsync(Guid symbolNodeId, int maxHops, CancellationToken ct) =>
            Task.FromResult(new ImpactSet([], [], []));
    }

    private sealed class ThrowingEmbeddingLookup : IEmbeddingLookup
    {
        public Task<float[]> EmbedAsync(Guid projectId, string text, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");

        public Task<float[][]> EmbedBatchAsync(Guid projectId, IReadOnlyList<string> texts, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }

    private sealed class FakeSearchIndex(
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double BM25Score)> lexical,
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double CosineScore)> vector) : ISearchIndex
    {
        public Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double BM25Score)>> LexicalSearchAsync(
            Guid projectId, string text, IReadOnlyList<string>? scope, CancellationToken ct) => Task.FromResult(lexical);

        public Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double CosineScore)>> VectorSearchAsync(
            Guid projectId, float[] queryEmbedding, IReadOnlyList<string>? scope, CancellationToken ct) => Task.FromResult(vector);
    }

    private sealed class FakeEmbeddingLookup : IEmbeddingLookup
    {
        public Task<float[]> EmbedAsync(Guid projectId, string text, CancellationToken ct) => Task.FromResult<float[]>([0.1f, 0.2f]);

        public Task<float[][]> EmbedBatchAsync(Guid projectId, IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult(texts.Select(_ => new[] { 0.1f, 0.2f }).ToArray());
    }

    private sealed class FakeReranker : IReranker
    {
        public Task<IReadOnlyList<int>> RerankAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>>([.. Enumerable.Range(0, candidates.Count)]);
    }

    private sealed class FakeGraphTraversal : IGraphTraversal
    {
        public Task<IReadOnlyList<GraphNode>> NeighborsAsync(Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphNode>>([]);

        public Task<GraphNeighborhood> NeighborhoodAsync(Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct) =>
            Task.FromResult(new GraphNeighborhood([], []));

        public Task<GraphNeighborhood> FullGraphAsync(Guid projectId, GraphEdgeKind[]? kinds, CancellationToken ct) =>
            Task.FromResult(new GraphNeighborhood([], []));

        public Task<IReadOnlyList<GraphNode>?> ShortestPathAsync(Guid fromId, Guid toId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphNode>?>([]);

        public Task<ImpactSet> BlastRadiusAsync(Guid symbolNodeId, int maxHops, CancellationToken ct) =>
            Task.FromResult(new ImpactSet([], [], []));
    }
}
