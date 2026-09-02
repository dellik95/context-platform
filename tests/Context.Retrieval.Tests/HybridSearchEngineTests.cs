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
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 10.0, (DateTimeOffset?)null), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 1.0, (DateTimeOffset?)null)],
            vector: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 0.0, (DateTimeOffset?)null), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 0.9, (DateTimeOffset?)null)]);

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
            lexical: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 10.0, (DateTimeOffset?)null), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 1.0, (DateTimeOffset?)null)],
            vector: [(Guid.NewGuid(), (Guid?)null, lexicalNode, "lexical-snippet", 0.0, (DateTimeOffset?)null), (Guid.NewGuid(), (Guid?)null, vectorNode, "vector-snippet", 0.9, (DateTimeOffset?)null)]);

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
    public async Task SearchAsync_recency_breaks_tie_between_otherwise_identical_candidates()
    {
        // Two candidates with identical lexical/vector raw scores (so lexNorm/vecNorm/graphNorm
        // all tie) but different owning-file UpdatedAt timestamps. With RecencyWeight > 0 (and
        // lexical/vector carrying the rest of the weight so the score isn't just recency alone),
        // the more recently updated candidate must rank first -- this is the real signal wired for
        // BACKLOG-13, replacing the old constant-0.5 placeholder that NormalizeMinMax could never
        // actually differentiate.
        var staleNode = Guid.NewGuid();
        var freshNode = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var searchIndex = new FakeSearchIndex(
            lexical:
            [
                (Guid.NewGuid(), (Guid?)null, staleNode, "same-snippet", 5.0, now - TimeSpan.FromDays(30)),
                (Guid.NewGuid(), (Guid?)null, freshNode, "same-snippet", 5.0, now),
            ],
            vector: []);

        var scorer = new HybridScorer(new HybridScoringOptions
        {
            LexicalWeight = 0.5,
            VectorWeight = 0,
            GraphWeight = 0,
            RecencyWeight = 0.5,
            MemoryWeight = 0
        });

        var sut = new HybridSearchEngine(
            searchIndex,
            new FakeEmbeddingLookup(),
            new FakeGraphTraversal(),
            new FakeReranker(),
            scorer,
            new HybridSearchOptions { RerankTopK = 1, SemanticBoostEnabled = false });

        var hits = await sut.SearchAsync(new SearchQuery(Guid.NewGuid(), "query", null, 1024), CancellationToken.None);

        hits.Should().HaveCount(2);
        hits[0].NodeId.Should().Be(freshNode, "the more recently updated candidate should win an otherwise-tied lexical/vector/graph score");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    private sealed class FakeSearchIndex(
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double BM25Score, DateTimeOffset? UpdatedAt)> lexical,
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double CosineScore, DateTimeOffset? UpdatedAt)> vector) : ISearchIndex
    {
        public Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double BM25Score, DateTimeOffset? UpdatedAt)>> LexicalSearchAsync(
            Guid projectId, string text, CancellationToken ct) => Task.FromResult(lexical);

        public Task<IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double CosineScore, DateTimeOffset? UpdatedAt)>> VectorSearchAsync(
            Guid projectId, float[] queryEmbedding, CancellationToken ct) => Task.FromResult(vector);
    }

    private sealed class FakeEmbeddingLookup : IEmbeddingLookup
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult<float[]>([0.1f, 0.2f]);

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
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
