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
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 10.0), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 1.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 0.0), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 0.9)]);

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
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 10.0), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 1.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 0.0), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 0.9)]);

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
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 10.0)],
            vector: [(Guid.NewGuid(), (Guid?)null, Guid.NewGuid(), "unreachable-vector-snippet", 0.9)]);

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

    private sealed class ThrowingEmbeddingLookup : IEmbeddingLookup
    {
        public Task<float[]> EmbedAsync(Guid projectId, string text, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");

        public Task<float[][]> EmbedBatchAsync(Guid projectId, IReadOnlyList<string> texts, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }

    private sealed class FakeSearchIndex(
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double BM25Score)> lexical,
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double CosineScore)> vector) : ISearchIndex
    {
        public Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double BM25Score)>> LexicalSearchAsync(
            Guid projectId, string text, CancellationToken ct) => Task.FromResult(lexical);

        public Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double CosineScore)>> VectorSearchAsync(
            Guid projectId, float[] queryEmbedding, CancellationToken ct) => Task.FromResult(vector);
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
