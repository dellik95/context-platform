using Context.Domain.Entities;
using Context.Graph.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;

namespace Context.Retrieval;

/// <summary>
/// Default <see cref="ISearchEngine"/> implementation (spec §9.6 pipeline): candidate retrieval
/// (lexical + vector) -&gt; merge/de-dup by node id -&gt; graph expansion of the top-N hits'
/// neighbors as extra candidates -&gt; <see cref="HybridScorer"/> -&gt; <see cref="IReranker"/> on
/// the top-K -&gt; ranked <see cref="SearchHit"/> list.
/// </summary>
/// <remarks>
/// Graph expansion assumes a candidate's Symbol/File id can be used directly as the seed node id
/// for <see cref="IGraphTraversal.NeighborsAsync"/>. <see cref="Context.Graph.Abstractions.IGraphTraversal"/>
/// is keyed by <c>GraphNode.Id</c> (a synthetic id), while lexical/vector candidates only carry
/// the referenced Symbol/File/Chunk id (<c>GraphNode.RefId</c>). If the two id spaces are not the
/// same value for a given entity, expansion silently finds no cache/neighbors for that seed (see
/// <c>GraphTraversal.NeighborsAsync</c>, which returns empty rather than throwing) and simply
/// contributes no graph signal — it never breaks the rest of the pipeline. Wiring a real
/// RefId-&gt;GraphNode.Id lookup (e.g. exposed from Context.Graph or Context.Infrastructure) is
/// flagged as follow-up work in the implementation report.
/// </remarks>
public sealed class HybridSearchEngine : ISearchEngine
{
    public const int DefaultGraphExpansionSeedCount = 5;
    public const int DefaultGraphExpansionHops = 1;
    public const int DefaultRerankTopK = 20;

    private readonly ISearchIndex _searchIndex;
    private readonly IEmbeddingLookup _embeddingLookup;
    private readonly IGraphTraversal _graphTraversal;
    private readonly IReranker _reranker;
    private readonly IHybridScorer _scorer;
    private readonly HybridSearchOptions _options;

    public HybridSearchEngine(
        ISearchIndex searchIndex,
        IEmbeddingLookup embeddingLookup,
        IGraphTraversal graphTraversal,
        IReranker reranker,
        IHybridScorer scorer,
        HybridSearchOptions? options = null)
    {
        _searchIndex = searchIndex;
        _embeddingLookup = embeddingLookup;
        _graphTraversal = graphTraversal;
        _reranker = reranker;
        _scorer = scorer;
        _options = options ?? new HybridSearchOptions();
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var lexicalTask = _searchIndex.LexicalSearchAsync(query.ProjectId, query.Text, ct);
        var queryEmbedding = await _embeddingLookup.EmbedAsync(query.Text, ct).ConfigureAwait(false);
        var vectorTask = _searchIndex.VectorSearchAsync(query.ProjectId, queryEmbedding, ct);

        var lexical = await lexicalTask.ConfigureAwait(false);
        var vector = await vectorTask.ConfigureAwait(false);

        var candidates = MergeCandidates(lexical, vector);
        if (candidates.Count == 0)
            return [];

        await ExpandViaGraphAsync(candidates, ct).ConfigureAwait(false);

        var ranked = ScoreAndRank(candidates);
        ranked = await RerankTopAsync(query.Text, ranked, ct).ConfigureAwait(false);

        return [.. ranked.Select(c => c.ToSearchHit())];
    }

    private static Dictionary<Guid, CandidateAccumulator> MergeCandidates(
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double BM25Score)> lexical,
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, string Snippet, double CosineScore)> vector)
    {
        var map = new Dictionary<Guid, CandidateAccumulator>();

        foreach (var r in lexical)
        {
            var key = r.SymbolId ?? r.FileId ?? r.ChunkId;
            var c = GetOrAdd(map, key, r.ChunkId, r.FileId, r.SymbolId);
            c.LexicalRaw = Math.Max(c.LexicalRaw, r.BM25Score);
            c.Snippet ??= r.Snippet;
        }

        foreach (var r in vector)
        {
            var key = r.SymbolId ?? r.FileId ?? r.ChunkId;
            var c = GetOrAdd(map, key, r.ChunkId, r.FileId, r.SymbolId);
            c.VectorRaw = Math.Max(c.VectorRaw, r.CosineScore);
            c.Snippet ??= r.Snippet;
        }

        return map;
    }

    private static CandidateAccumulator GetOrAdd(
        Dictionary<Guid, CandidateAccumulator> map, Guid key, Guid chunkId, Guid? fileId, Guid? symbolId)
    {
        if (!map.TryGetValue(key, out var c))
        {
            c = new CandidateAccumulator(key) { ChunkId = chunkId, FileId = fileId, SymbolId = symbolId };
            map[key] = c;
        }

        return c;
    }

    private async Task ExpandViaGraphAsync(Dictionary<Guid, CandidateAccumulator> candidates, CancellationToken ct)
    {
        var seedIds = candidates.Values
            .OrderByDescending(c => c.LexicalRaw + c.VectorRaw)
            .Take(_options.GraphExpansionSeedCount)
            .Select(c => c.NodeId)
            .ToList();

        foreach (var seedId in seedIds)
        {
            ct.ThrowIfCancellationRequested();

            if (candidates.TryGetValue(seedId, out var seed))
                seed.GraphHopDistance = 0;

            IReadOnlyList<GraphNode> neighbors;
            try
            {
                neighbors = await _graphTraversal
                    .NeighborsAsync(seedId, null, _options.GraphExpansionHops, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Graph expansion is a best-effort enrichment signal only; a failing/unavailable
                // graph must never take down lexical+vector retrieval.
                neighbors = [];
            }

            foreach (var neighbor in neighbors)
            {
                var key = neighbor.RefId ?? neighbor.Id;
                if (!candidates.TryGetValue(key, out var existing))
                {
                    existing = new CandidateAccumulator(key);
                    candidates[key] = existing;
                }

                existing.GraphKind ??= neighbor.Kind.ToString();
                existing.GraphLabel ??= neighbor.Label;
                if (existing.GraphHopDistance is null || existing.GraphHopDistance > 1)
                    existing.GraphHopDistance = 1;
            }
        }
    }

    private List<CandidateAccumulator> ScoreAndRank(Dictionary<Guid, CandidateAccumulator> candidates)
    {
        var list = candidates.Values.ToList();

        var lexNorm = HybridScorer.NormalizeMinMax([.. list.Select(c => c.LexicalRaw)]);
        var vecNorm = HybridScorer.NormalizeMinMax([.. list.Select(c => c.VectorRaw)]);
        var graphRaw = list.Select(c => c.GraphHopDistance switch { 0 => 1.0, 1 => 0.5, _ => 0.0 }).ToList();
        var graphNorm = HybridScorer.NormalizeMinMax(graphRaw);

        // Recency and memory signals have no data source available at this layer yet (no file
        // timestamps flow through ISearchIndex; Context.Memory is not wired into ISearchEngine).
        // They are supplied as a flat placeholder batch, which NormalizeMinMax turns into a
        // uniform 0.5 contribution per candidate — i.e. a no-op on relative ranking until a real
        // signal is threaded in (see implementation report follow-up).
        var recencyNorm = HybridScorer.NormalizeMinMax([.. list.Select(_ => 0.5)]);
        var memoryNorm = HybridScorer.NormalizeMinMax([.. list.Select(_ => 0.0)]);

        for (var i = 0; i < list.Count; i++)
        {
            var signals = new NormalizedSignals(lexNorm[i], vecNorm[i], graphNorm[i], recencyNorm[i], memoryNorm[i]);
            list[i].Score = _scorer.Combine(signals);
        }

        return [.. list.OrderByDescending(c => c.Score)];
    }

    private async Task<List<CandidateAccumulator>> RerankTopAsync(
        string queryText, List<CandidateAccumulator> ranked, CancellationToken ct)
    {
        var topCount = Math.Min(_options.RerankTopK, ranked.Count);
        if (topCount <= 1)
            return ranked;

        var top = ranked.Take(topCount).ToList();
        var texts = top.Select(c => c.Snippet ?? c.GraphLabel ?? string.Empty).ToList();

        IReadOnlyList<int> order;
        try
        {
            order = await _reranker.RerankAsync(queryText, texts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Reranking is a refinement pass; a failing reranker must not lose the hybrid-scored
            // candidates, so fall back to the pre-rerank order.
            return ranked;
        }

        if (order.Count != top.Count || order.Distinct().Count() != top.Count || order.Any(i => i < 0 || i >= top.Count))
            return ranked;

        var reordered = order.Select(i => top[i]).ToList();
        reordered.AddRange(ranked.Skip(topCount));
        return reordered;
    }

    private sealed class CandidateAccumulator(Guid nodeId)
    {
        public Guid NodeId { get; } = nodeId;
        public Guid? ChunkId { get; set; }
        public Guid? FileId { get; set; }
        public Guid? SymbolId { get; set; }
        public string? Snippet { get; set; }
        public string? GraphKind { get; set; }
        public string? GraphLabel { get; set; }
        public double LexicalRaw { get; set; }
        public double VectorRaw { get; set; }
        public int? GraphHopDistance { get; set; }
        public double Score { get; set; }

        public SearchHit ToSearchHit()
        {
            var kind = GraphKind ?? (SymbolId is not null ? "Symbol" : FileId is not null ? "File" : "Chunk");
            var label = GraphLabel ?? DeriveLabel();
            return new SearchHit(NodeId, kind, label, Score, Snippet);
        }

        private string DeriveLabel()
        {
            if (string.IsNullOrWhiteSpace(Snippet))
                return $"{(SymbolId is not null ? "Symbol" : FileId is not null ? "File" : "Chunk")} {NodeId}";

            var firstLine = Snippet.AsSpan().TrimStart();
            var newline = firstLine.IndexOfAny('\r', '\n');
            if (newline >= 0)
                firstLine = firstLine[..newline];

            const int maxLabelLength = 96;
            return firstLine.Length > maxLabelLength
                ? string.Concat(firstLine[..maxLabelLength], "…")
                : firstLine.ToString();
        }
    }
}
