using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Microsoft.Extensions.Options;

namespace Context.Retrieval;

/// <summary>
/// Default <see cref="ISearchEngine"/> implementation (spec §9.6 pipeline): candidate retrieval
/// (lexical + vector) -&gt; merge/de-dup by node id -&gt; graph expansion of the top-N hits'
/// neighbors as extra candidates -&gt; <see cref="HybridScorer"/> -&gt; <see cref="IReranker"/> on
/// the top-K -&gt; ranked <see cref="SearchHit"/> list.
/// </summary>
/// <remarks>
/// Graph expansion is best-effort enrichment and does not fail retrieval if graph lookups miss.
/// </remarks>
public sealed class HybridSearchEngine : ISearchEngine
{
    public const int DefaultGraphExpansionSeedCount = 5;
    public const int DefaultGraphExpansionHops = 1;
    public const int DefaultRerankTopK = 20;
    public const double DefaultSemanticStrongMatchThreshold = 0.80;
    public const double DefaultSemanticBoostFactor = 0.12;
    public const double DefaultVectorRelevanceFloor = 0.25;

    private readonly ISearchIndex _searchIndex;
    private readonly IEmbeddingLookup _embeddingLookup;
    private readonly IGraphTraversal _graphTraversal;
    private readonly IGraphCacheProvider? _graphCacheProvider;
    private readonly IReranker _reranker;
    private readonly IHybridScorer _scorer;
    private readonly HybridSearchOptions _options;
    private readonly IOptionsMonitor<HybridSearchOptions>? _optionsMonitor;

    public HybridSearchEngine(
        ISearchIndex searchIndex,
        IEmbeddingLookup embeddingLookup,
        IGraphTraversal graphTraversal,
        IReranker reranker,
        IHybridScorer scorer,
        HybridSearchOptions? options = null,
        IGraphCacheProvider? graphCacheProvider = null)
    {
        _searchIndex = searchIndex;
        _embeddingLookup = embeddingLookup;
        _graphTraversal = graphTraversal;
        _reranker = reranker;
        _scorer = scorer;
        _options = options ?? new HybridSearchOptions();
        _graphCacheProvider = graphCacheProvider;
    }

    public HybridSearchEngine(
        ISearchIndex searchIndex,
        IEmbeddingLookup embeddingLookup,
        IGraphTraversal graphTraversal,
        IReranker reranker,
        IHybridScorer scorer,
        IOptionsMonitor<HybridSearchOptions> optionsMonitor,
        IGraphCacheProvider? graphCacheProvider = null)
        : this(searchIndex, embeddingLookup, graphTraversal, reranker, scorer, optionsMonitor.CurrentValue, graphCacheProvider)
    {
        _optionsMonitor = optionsMonitor;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var lexicalTask = _searchIndex.LexicalSearchAsync(query.ProjectId, query.Text, query.Scope, ct);

        float[]? queryEmbedding;
        try
        {
            queryEmbedding = await _embeddingLookup.EmbedAsync(query.ProjectId, query.Text, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Embedding provider (e.g. Ollama) may be unreachable/misconfigured; vector search is
            // an enrichment signal only and must never take down lexical retrieval.
            queryEmbedding = null;
        }

        var vector = queryEmbedding is null
            ? []
            : await _searchIndex.VectorSearchAsync(query.ProjectId, queryEmbedding, query.Scope, ct).ConfigureAwait(false);

        var lexical = await lexicalTask.ConfigureAwait(false);

        var candidates = MergeCandidates(lexical, vector);
        DropIrrelevantVectorSignal(candidates, CurrentOptions.VectorRelevanceFloor);
        if (candidates.Count == 0)
            return [];

        // Graph neighbors are nodes, with no path or language attached at this layer, so a scoped
        // query skips expansion rather than smuggling out-of-scope files back into the results.
        if (query.Scope is null or [])
        {
            await WarmGraphCacheBestEffortAsync(query.ProjectId, ct).ConfigureAwait(false);
            await ExpandViaGraphAsync(candidates, ct).ConfigureAwait(false);
        }

        var ranked = ScoreAndRank(candidates);
        ranked = await RerankTopAsync(query.Text, ranked, ct).ConfigureAwait(false);

        return [.. ranked.Select(c => c.ToSearchHit())];
    }

    private async Task WarmGraphCacheBestEffortAsync(Guid projectId, CancellationToken ct)
    {
        if (_graphCacheProvider is null)
            return;

        try
        {
            await _graphCacheProvider.GetOrCreateAsync(projectId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Graph expansion is best-effort enrichment only.
        }
    }

    private static Dictionary<Guid, CandidateAccumulator> MergeCandidates(
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double BM25Score, string? Label, string? RelativePath)> lexical,
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double CosineScore, string? Label, string? RelativePath)> vector)
    {
        var map = new Dictionary<Guid, CandidateAccumulator>();

        foreach (var r in lexical)
        {
            var key = r.SymbolId ?? r.FileId ?? r.ChunkId;
            var c = GetOrAdd(map, key, r.ChunkId, r.FileId, r.SymbolId, r.SymbolKind);
            c.LexicalRaw = Math.Max(c.LexicalRaw, r.BM25Score);
            c.Snippet ??= r.Snippet;
            c.IndexLabel ??= r.Label;
        }

        foreach (var r in vector)
        {
            var key = r.SymbolId ?? r.FileId ?? r.ChunkId;
            var c = GetOrAdd(map, key, r.ChunkId, r.FileId, r.SymbolId, r.SymbolKind);
            c.VectorRaw = Math.Max(c.VectorRaw, r.CosineScore);
            c.Snippet ??= r.Snippet;
            c.IndexLabel ??= r.Label;
        }

        return map;
    }

    /// <summary>
    /// Zeroes cosine scores below <paramref name="floor"/> and removes candidates left with no
    /// signal at all, before normalization and graph expansion see them.
    /// </summary>
    /// <remarks>
    /// The ANN pass always returns its top-K, however far away those neighbors actually are, and
    /// <see cref="HybridScorer.NormalizeMinMax"/> then stretches whatever came back to fill [0, 1]
    /// — so the least-bad hit of an entirely irrelevant result set normalizes to 1.0 and outranks a
    /// genuine but lower-ranked lexical match. Cosine is the one signal here with an absolute
    /// scale, so it is the one that can be floored; a query with no real semantic match now returns
    /// its lexical hits alone, or nothing, instead of confident-looking noise.
    /// </remarks>
    private static void DropIrrelevantVectorSignal(Dictionary<Guid, CandidateAccumulator> candidates, double floor)
    {
        foreach (var (key, candidate) in candidates)
        {
            if (candidate.VectorRaw >= floor)
                continue;

            candidate.VectorRaw = 0.0;
            if (candidate.LexicalRaw <= 0.0)
                candidates.Remove(key);
        }
    }

    private static CandidateAccumulator GetOrAdd(
        Dictionary<Guid, CandidateAccumulator> map, Guid key, Guid chunkId, Guid? fileId, Guid? symbolId, SymbolKind? symbolKind)
    {
        if (!map.TryGetValue(key, out var c))
        {
            c = new CandidateAccumulator(key) { ChunkId = chunkId, FileId = fileId, SymbolId = symbolId, SymbolKind = symbolKind };
            map[key] = c;
        }
        else
        {
            c.SymbolKind ??= symbolKind;
        }

        return c;
    }

    private async Task ExpandViaGraphAsync(Dictionary<Guid, CandidateAccumulator> candidates, CancellationToken ct)
    {
        var options = CurrentOptions;

        var seedIds = candidates.Values
            .OrderByDescending(c => c.LexicalRaw + c.VectorRaw)
            .Take(options.GraphExpansionSeedCount)
            .Select(c => c.NodeId)
            .ToList();

        foreach (var seedId in seedIds)
        {
            ct.ThrowIfCancellationRequested();

            // The seed deliberately gets no graph credit for being its own seed: it was chosen for
            // already scoring highest on lexical+vector, so crediting it again is the same evidence
            // counted twice, and the top-5 would compound their own lead over everything else.
            // Graph proximity only says something new about a candidate reached *through* a seed.
            IReadOnlyList<GraphNode> neighbors;
            try
            {
                neighbors = await _graphTraversal
                    .NeighborsAsync(seedId, null, options.GraphExpansionHops, ct)
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
        var options = CurrentOptions;
        var list = candidates.Values.ToList();

        var lexNorm = HybridScorer.NormalizeMinMax([.. list.Select(c => c.LexicalRaw)]);
        var vecNorm = HybridScorer.NormalizeMinMax([.. list.Select(c => c.VectorRaw)]);

        // Graph proximity is already a bounded [0, 1] quantity by construction (a neighbor of a top
        // hit is worth half a direct hit), so it is NOT min-max normalized: doing that would stretch
        // the constant 0.5 to a full 1.0 whenever any neighbor was found, and hand every candidate
        // an identical 0.5 when none was -- a signal that only ever reports how the batch happened
        // to be shaped, never how close this candidate is.
        var graphSignal = list.Select(c => c.GraphHopDistance == 1 ? 0.5 : 0.0).ToList();

        // Recency: File.UpdatedAt was tried as a per-candidate signal here and measured
        // against RetrievalQualityHarnessTests, but it regressed hit-rate@5/MRR: UpdatedAt is stamped
        // at index-processing time, not source-content-change time (it already means "last indexed"
        // elsewhere -- see ContextEngine.ProjectInfoAsync's LastIndexedAt), so a full reindex gives
        // every candidate a near-identical "just indexed" timestamp that NormalizeMinMax then
        // stretches into pure noise. Reverted to a constant placeholder, same honest-zero treatment
        // as memory below, pending a genuinely separate source-last-modified signal.
        // Memory: no per-candidate memory-adjacency data source exists for code search today
        // (Memory.FilesReadJson/FilesModifiedJson are write-only path lists with no FK to
        // Chunk/File). Both this and recency are passed as a literal 0.0 rather than through
        // NormalizeMinMax, whose all-equal case would turn a placeholder into a 0.5 contribution;
        // their weights are 0.0 as well, so they are inert twice over until a real signal exists.
        for (var i = 0; i < list.Count; i++)
        {
            var signals = new NormalizedSignals(lexNorm[i], vecNorm[i], graphSignal[i], 0.0, 0.0);
            var score = _scorer.Combine(signals);

            // Keyed off raw cosine, not the normalized value: the threshold is a statement about
            // absolute semantic closeness, and the best hit in any batch normalizes to 1.0 no
            // matter how weak it is, so the normalized form boosted one candidate on every query.
            if (options.SemanticBoostEnabled && list[i].VectorRaw >= options.SemanticStrongMatchThreshold)
            {
                score = Math.Clamp(score + (options.SemanticBoostFactor * list[i].VectorRaw), 0.0, 1.0);
            }

            list[i].Score = score;
        }

        return [.. list.OrderByDescending(c => c.Score)];
    }

    private async Task<List<CandidateAccumulator>> RerankTopAsync(
        string queryText, List<CandidateAccumulator> ranked, CancellationToken ct)
    {
        var topCount = Math.Min(CurrentOptions.RerankTopK, ranked.Count);
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
        if (!IsIdentityOrder(order))
        {
            var minCutoff = ranked.Count > topCount ? ranked[topCount].Score : 0.0;
            for (var i = 0; i < reordered.Count; i++)
            {
                var rankScore = 1.0 - ((double)i / Math.Max(1, reordered.Count)) * Math.Max(0.05, 1.0 - minCutoff);
                var blended = Math.Round(Math.Clamp((0.5 * rankScore) + (0.5 * reordered[i].Score), minCutoff, 1.0), 6);
                reordered[i].Score = i > 0 && blended > reordered[i - 1].Score
                    ? reordered[i - 1].Score
                    : blended;
            }
        }

        reordered.AddRange(ranked.Skip(topCount));
        return reordered;
    }

    private static bool IsIdentityOrder(IReadOnlyList<int> order)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] != i)
                return false;
        }

        return true;
    }

    private HybridSearchOptions CurrentOptions => _optionsMonitor?.CurrentValue ?? _options;

    private sealed class CandidateAccumulator(Guid nodeId)
    {
        public Guid NodeId { get; } = nodeId;
        public Guid? ChunkId { get; set; }
        public Guid? FileId { get; set; }
        public Guid? SymbolId { get; set; }
        public SymbolKind? SymbolKind { get; set; }
        public string? Snippet { get; set; }
        public string? IndexLabel { get; set; }
        public string? GraphKind { get; set; }
        public string? GraphLabel { get; set; }
        public double LexicalRaw { get; set; }
        public double VectorRaw { get; set; }
        public int? GraphHopDistance { get; set; }
        public double Score { get; set; }

        public SearchHit ToSearchHit()
        {
            var kind = GraphKind ?? DefaultKind();
            var label = GraphLabel ?? IndexLabel ?? DeriveLabel();
            return new SearchHit(NodeId, kind, label, Score, Snippet);
        }

        // SymbolKind (Class/Method/Function/...) when this hit resolved to a symbol -- falls back to
        // the generic "Symbol" only for the rare case a chunk has a SymbolId but that symbol's row
        // wasn't found by the Kind join (e.g. deleted between search and this candidate being built).
        private string DefaultKind() =>
            SymbolId is not null ? (SymbolKind?.ToString() ?? "Symbol") : FileId is not null ? "File" : "Chunk";

        private string DeriveLabel()
        {
            if (string.IsNullOrWhiteSpace(Snippet))
                return $"{DefaultKind()} {NodeId}";

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
