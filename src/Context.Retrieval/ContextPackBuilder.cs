using Context.Retrieval.Abstractions;
using Context.Shared;

namespace Context.Retrieval;

/// <summary>
/// Default <see cref="IContextPackBuilder"/> implementation. Runs <see cref="ISearchEngine"/>,
/// then greedily fills the sectioned token budget from spec §9.7 (architecture / primary hits /
/// historical notes / open issues / safety reserve) by score-to-token ratio, per section.
/// </summary>
/// <remarks>
/// Context.Retrieval has no access to memories, so the interface overload builds the historical
/// notes and open issues sections empty; Context.Application passes them in through the richer
/// <see cref="BuildAsync(SearchQuery, IReadOnlyList{string}?, IReadOnlyList{string}?, CancellationToken)"/>.
/// </remarks>
public sealed class ContextPackBuilder : IContextPackBuilder
{
    public const double DefaultArchitectureReserveRatio = 0.10;
    public const double DefaultPrimaryReserveRatio = 0.45;
    public const double DefaultHistoricalReserveRatio = 0.25;
    public const double DefaultOpenIssuesReserveRatio = 0.10;
    public const double DefaultSafetyReserveRatio = 0.10;

    private const int MaxArchitectureChainLength = 6;

    private readonly ISearchEngine _searchEngine;
    private readonly ContextPackOptions _options;

    public ContextPackBuilder(ISearchEngine searchEngine, ContextPackOptions? options = null)
    {
        _searchEngine = searchEngine;
        _options = options ?? new ContextPackOptions();
    }

    public Task<ContextPack> BuildAsync(SearchQuery query, CancellationToken ct) =>
        BuildAsync(query, historicalNotes: null, openIssues: null, ct);

    /// <summary>
    /// Same as <see cref="BuildAsync(SearchQuery, CancellationToken)"/>, with caller-ordered
    /// historical-notes and open-issues sections.
    /// </summary>
    public async Task<ContextPack> BuildAsync(
        SearchQuery query, IReadOnlyList<string>? historicalNotes, IReadOnlyList<string>? openIssues, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var hits = await _searchEngine.SearchAsync(query, ct).ConfigureAwait(false);

        var budget = Math.Max(0, query.TokenBudget);
        // The safety reserve (~10%) is intentionally never spent — it is headroom, not a section.
        // Every other section hands what it did not spend to the next one, and primary hits are
        // filled last so they inherit all of it: historical notes and open issues are empty on the
        // IContextPackBuilder-contract overload and frequently empty in practice, and without a
        // carry-forward their 35% of the budget was simply burned, returning a pack a third smaller
        // than the caller asked for while the search had more hits to give.
        var architectureBudget = (int)(budget * _options.ArchitectureReserveRatio);
        var architectureSummary = BuildArchitectureSummary(hits, architectureBudget);
        var architectureTokens = EstimateSection(architectureSummary);
        var unspent = Math.Max(0, architectureBudget - architectureTokens);

        // A "what's still pending?" query moves pending-work flavored notes ahead of the caller's
        // order within each section before the knapsack decides what fits.
        var pendingWorkIntent = QueryIntent.IsPendingWork(query.Text);

        var historicalBudget = (int)(budget * _options.HistoricalReserveRatio) + unspent;
        var (historicalSelected, historicalTokens, historicalTruncated, historicalMore) =
            GreedyKnapsackOrdered(PrioritizeIfPendingWork(historicalNotes ?? [], pendingWorkIntent), TokenEstimator.Estimate, historicalBudget);
        unspent = Math.Max(0, historicalBudget - historicalTokens);

        var openIssuesBudget = (int)(budget * _options.OpenIssuesReserveRatio) + unspent;
        var (openIssuesSelected, openIssuesTokens, openIssuesTruncated, openIssuesMore) =
            GreedyKnapsackOrdered(PrioritizeIfPendingWork(openIssues ?? [], pendingWorkIntent), TokenEstimator.Estimate, openIssuesBudget);
        unspent = Math.Max(0, openIssuesBudget - openIssuesTokens);

        var primaryBudget = (int)(budget * _options.PrimaryReserveRatio) + unspent;
        var (primaryHits, primaryTokens, primaryTruncated, primaryMore) =
            GreedyKnapsack(hits, h => h.Score, h => TokenEstimator.Estimate(h.Snippet ?? h.Label), primaryBudget);

        var estimatedTokens = architectureTokens + primaryTokens + historicalTokens + openIssuesTokens;

        var truncated = primaryTruncated || historicalTruncated || openIssuesTruncated;
        var moreAvailable = primaryMore + historicalMore + openIssuesMore;

        var signature = BuildSignature(primaryHits);

        return new ContextPack(
            architectureSummary,
            primaryHits,
            historicalSelected,
            openIssuesSelected,
            estimatedTokens,
            truncated,
            moreAvailable,
            signature);
    }

    /// <summary>
    /// Stable-partitions a pre-ordered notes/issues section so pending-work-flavored items (per
    /// <see cref="QueryIntent.IsPendingWorkFlavored"/>) come first, preserving the caller's
    /// relative order within each partition -- a no-op unless <paramref name="pendingWorkIntent"/>
    /// is set. Runs before <see cref="GreedyKnapsackOrdered"/> so a token-constrained budget is
    /// more likely to include the pending-flavored items first.
    /// </summary>
    private static IReadOnlyList<string> PrioritizeIfPendingWork(IReadOnlyList<string> items, bool pendingWorkIntent) =>
        pendingWorkIntent ? [.. items.OrderByDescending(QueryIntent.IsPendingWorkFlavored)] : items;

    /// <summary>
    /// <see cref="TokenEstimator.Estimate(string)"/> floors at 1 token, so an absent section would
    /// otherwise bill the pack — and, via the carry-forward, shrink every section after it.
    /// </summary>
    private static int EstimateSection(string text) =>
        string.IsNullOrEmpty(text) ? 0 : TokenEstimator.Estimate(text);

    private static string BuildArchitectureSummary(IReadOnlyList<SearchHit> hits, int budgetTokens)
    {
        if (hits.Count == 0 || budgetTokens <= 0)
            return string.Empty;

        var chain = new List<string>();
        var used = 0;

        foreach (var hit in hits.OrderByDescending(h => h.Score))
        {
            if (chain.Count >= MaxArchitectureChainLength)
                break;

            var label = string.IsNullOrWhiteSpace(hit.Label) ? hit.NodeId.ToString() : hit.Label;
            if (chain.Contains(label))
                continue;

            var separatorCost = chain.Count == 0 ? 0 : TokenEstimator.Estimate(" -> ");
            var cost = separatorCost + TokenEstimator.Estimate(label);

            if (used + cost > budgetTokens && chain.Count > 0)
                break;

            chain.Add(label);
            used += cost;
        }

        return string.Join(" -> ", chain);
    }

    /// <summary>
    /// Budget-constrained selection that guarantees the highest-scoring hit first (if it fits),
    /// fills remaining budget by score-to-floored-token-cost ratio (preventing tiny low-score
    /// fragments from outranking full method hits), and returns selected items ordered strictly by
    /// relevance <c>score</c> descending.
    /// </summary>
    private static (IReadOnlyList<T> Selected, int TokensUsed, bool Truncated, int MoreAvailable) GreedyKnapsack<T>(
        IReadOnlyList<T> items, Func<T, double> score, Func<T, int> tokenCost, int budget)
    {
        if (items.Count == 0 || budget <= 0)
        {
            return ([], 0, items.Count > 0, items.Count);
        }

        const int minCostFloorForRatio = 20;
        var indexed = items
            .Select((item, idx) => (Item: item, Cost: Math.Max(1, tokenCost(item)), Score: score(item), Index: idx))
            .ToList();

        var selected = new List<(T Item, double Score)>();
        var usedIndices = new HashSet<int>();
        var used = 0;

        // Guarantee the top-scoring hit when it fits so medium-scoring small chunks cannot crowd it out.
        var topByScore = indexed
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .First();
        if (topByScore.Cost <= budget)
        {
            selected.Add((topByScore.Item, topByScore.Score));
            usedIndices.Add(topByScore.Index);
            used += topByScore.Cost;
        }

        var remainingByDensity = indexed
            .Where(x => !usedIndices.Contains(x.Index))
            .OrderByDescending(x => x.Score / Math.Max(minCostFloorForRatio, x.Cost))
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Index);

        foreach (var x in remainingByDensity)
        {
            if (used + x.Cost > budget)
                continue;

            selected.Add((x.Item, x.Score));
            used += x.Cost;
        }

        var orderedSelected = selected
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();

        var truncated = orderedSelected.Count < items.Count;
        return (orderedSelected, used, truncated, items.Count - orderedSelected.Count);
    }

    /// <summary>
    /// Budget-fill for pre-ordered plain-text sections (historical notes, open issues) where the
    /// caller's ordering already reflects relevance/recency — items are taken front-to-back
    /// (skipping ones too big to fit, same as <see cref="GreedyKnapsack{T}"/>) rather than
    /// re-sorted by a score this layer does not have.
    /// </summary>
    private static (IReadOnlyList<string> Selected, int TokensUsed, bool Truncated, int MoreAvailable) GreedyKnapsackOrdered(
        IReadOnlyList<string> items, Func<string, int> tokenCost, int budget)
    {
        var selected = new List<string>();
        var used = 0;

        foreach (var item in items)
        {
            var cost = Math.Max(1, tokenCost(item));
            if (used + cost > budget)
                continue;

            selected.Add(item);
            used += cost;
        }

        var truncated = selected.Count < items.Count;
        return (selected, used, truncated, items.Count - selected.Count);
    }

    private static string BuildSignature(IReadOnlyList<SearchHit> includedHits)
    {
        var sortedIds = includedHits.Select(h => h.NodeId).OrderBy(id => id).ToList();
        return Hashing.HashText(string.Join(",", sortedIds));
    }
}
