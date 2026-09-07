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

    private readonly ISearchIndex _searchIndex;
    private readonly IEmbeddingLookup _embeddingLookup;
    private readonly IGraphTraversal _graphTraversal;
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
        HybridSearchOptions? options = null)
    {
        _searchIndex = searchIndex;
        _embeddingLookup = embeddingLookup;
        _graphTraversal = graphTraversal;
        _reranker = reranker;
        _scorer = scorer;
        _options = options ?? new HybridSearchOptions();
    }

    public HybridSearchEngine(
        ISearchIndex searchIndex,
        IEmbeddingLookup embeddingLookup,
        IGraphTraversal graphTraversal,
        IReranker reranker,
        IHybridScorer scorer,
        IOptionsMonitor<HybridSearchOptions> optionsMonitor)
        : this(searchIndex, embeddingLookup, graphTraversal, reranker, scorer, optionsMonitor.CurrentValue)
    {
        _optionsMonitor = optionsMonitor;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var lexicalTask = _searchIndex.LexicalSearchAsync(query.ProjectId, query.Text, ct);

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
            : await _searchIndex.VectorSearchAsync(query.ProjectId, queryEmbedding, ct).ConfigureAwait(false);

        var lexical = await lexicalTask.ConfigureAwait(false);

        var candidates = MergeCandidates(lexical, vector);
        if (candidates.Count == 0)
            return [];

        await ExpandViaGraphAsync(candidates, ct).ConfigureAwait(false);

        var ranked = ScoreAndRank(candidates);
        ranked = await RerankTopAsync(query.Text, ranked, ct).ConfigureAwait(false);

        return [.. ranked.Select(c => c.ToSearchHit())];
    }

    private static Dictionary<Guid, CandidateAccumulator> MergeCandidates(
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double BM25Score)> lexical,
        IReadOnlyList<(Guid ChunkId, Guid? FileId, Guid? SymbolId, SymbolKind? SymbolKind, string Snippet, double CosineScore)> vector)
    {
        var map = new Dictionary<Guid, CandidateAccumulator>();

        foreach (var r in lexical)
        {
            var key = r.SymbolId ?? r.FileId ?? r.ChunkId;
            var c = GetOrAdd(map, key, r.ChunkId, r.FileId, r.SymbolId, r.SymbolKind);
            c.LexicalRaw = Math.Max(c.LexicalRaw, r.BM25Score);
            c.Snippet ??= r.Snippet;
        }

        foreach (var r in vector)
        {
            var key = r.SymbolId ?? r.FileId ?? r.ChunkId;
            var c = GetOrAdd(map, key, r.ChunkId, r.FileId, r.SymbolId, r.SymbolKind);
            c.VectorRaw = Math.Max(c.VectorRaw, r.CosineScore);
            c.Snippet ??= r.Snippet;
        }

        return map;
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

            if (candidates.TryGetValue(seedId, out var seed))
                seed.GraphHopDistance = 0;

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
        var graphRaw = list.Select(c => c.GraphHopDistance switch { 0 => 1.0, 1 => 0.5, _ => 0.0 }).ToList();
        var graphNorm = HybridScorer.NormalizeMinMax(graphRaw);

        // Recency: File.UpdatedAt was tried as a per-candidate signal here (BACKLOG-13) and measured
        // against RetrievalQualityHarnessTests, but it regressed hit-rate@5/MRR: UpdatedAt is stamped
        // at index-processing time, not source-content-change time (it already means "last indexed"
        // elsewhere -- see ContextEngine.ProjectInfoAsync's LastIndexedAt), so a full reindex gives
        // every candidate a near-identical "just indexed" timestamp that NormalizeMinMax then
        // stretches into pure noise. Reverted to a constant placeholder, same honest-zero treatment
        // as memory below, pending a genuinely separate source-last-modified signal.
        var recencyNorm = HybridScorer.NormalizeMinMax([.. list.Select(_ => 0.0)]);

        // Memory: no per-candidate memory-adjacency data source exists for code search today (see
        // ARCHITECTURE_ASSESSMENT.md RAG-1/BACKLOG-13 -- Memory.FilesReadJson/FilesModifiedJson are
        // write-only path lists with no FK to Chunk/File). This stays a constant placeholder, but
        // HybridScoringOptions.MemoryWeight now defaults to 0.0 so it is honestly inert rather than
        // silently contributing a non-zero constant via NormalizeMinMax's all-equal midpoint.
        var memoryNorm = HybridScorer.NormalizeMinMax([.. list.Select(_ => 0.0)]);

        for (var i = 0; i < list.Count; i++)
        {
            var signals = new NormalizedSignals(lexNorm[i], vecNorm[i], graphNorm[i], recencyNorm[i], memoryNorm[i]);
            var score = _scorer.Combine(signals);
            if (options.SemanticBoostEnabled && vecNorm[i] >= options.SemanticStrongMatchThreshold)
            {
                score = Math.Clamp(score + (options.SemanticBoostFactor * vecNorm[i]), 0.0, 1.0);
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
        reordered.AddRange(ranked.Skip(topCount));
        return reordered;
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
        public string? GraphKind { get; set; }
        public string? GraphLabel { get; set; }
        public double LexicalRaw { get; set; }
        public double VectorRaw { get; set; }
        public int? GraphHopDistance { get; set; }
        public double Score { get; set; }

        public SearchHit ToSearchHit()
        {
            var kind = GraphKind ?? DefaultKind();
            var label = GraphLabel ?? DeriveLabel();
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
