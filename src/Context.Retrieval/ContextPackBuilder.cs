using Context.Retrieval.Abstractions;
using Context.Shared;

namespace Context.Retrieval;

/// <summary>
/// Default <see cref="IContextPackBuilder"/> implementation. Runs <see cref="ISearchEngine"/>,
/// then greedily fills the sectioned token budget from spec §9.7 (architecture / primary hits /
/// historical notes / open issues / safety reserve) by score-to-token ratio, per section.
/// </summary>
/// <remarks>
/// Context.Memory is not wired into Context.Retrieval (see CONTRACTS.md dependency graph), so
/// there is no in-process source for "historical context" or "open issues" yet. The
/// interface-mandated <see cref="BuildAsync(SearchQuery, CancellationToken)"/> therefore builds
/// those two sections empty; the additional <see cref="BuildAsync(SearchQuery, IReadOnlyList{string}?, IReadOnlyList{string}?, CancellationToken)"/>
/// overload lets a caller that already has Memory results (Context.Application, once it wires
/// Context.Memory) pass them in directly. This is a documented seam, not part of the fixed
/// <c>IContextPackBuilder</c> contract.
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
    /// Same as <see cref="BuildAsync(SearchQuery, CancellationToken)"/>, but lets a caller with
    /// access to Context.Memory results supply the historical-notes and open-issues sections
    /// directly, since Context.Retrieval has no path to that data on its own yet.
    /// </summary>
    public async Task<ContextPack> BuildAsync(
        SearchQuery query, IReadOnlyList<string>? historicalNotes, IReadOnlyList<string>? openIssues, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var hits = await _searchEngine.SearchAsync(query, ct).ConfigureAwait(false);

        var budget = Math.Max(0, query.TokenBudget);
        var architectureBudget = (int)(budget * _options.ArchitectureReserveRatio);
        var primaryBudget = (int)(budget * _options.PrimaryReserveRatio);
        var historicalBudget = (int)(budget * _options.HistoricalReserveRatio);
        var openIssuesBudget = (int)(budget * _options.OpenIssuesReserveRatio);
        // The safety reserve (~10%) is intentionally never spent — it is headroom, not a section.

        var architectureSummary = BuildArchitectureSummary(hits, architectureBudget);

        var (primaryHits, primaryTokens, primaryTruncated, primaryMore) =
            GreedyKnapsack(hits, h => h.Score, h => TokenEstimator.Estimate(h.Snippet ?? h.Label), primaryBudget);

        var (historicalSelected, historicalTokens, historicalTruncated, historicalMore) =
            GreedyKnapsackOrdered(historicalNotes ?? [], TokenEstimator.Estimate, historicalBudget);

        var (openIssuesSelected, openIssuesTokens, openIssuesTruncated, openIssuesMore) =
            GreedyKnapsackOrdered(openIssues ?? [], TokenEstimator.Estimate, openIssuesBudget);

        var estimatedTokens =
            TokenEstimator.Estimate(architectureSummary) + primaryTokens + historicalTokens + openIssuesTokens;

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
    /// Classic greedy 0/1-knapsack approximation: sort by score/token ratio descending, then add
    /// each item that still fits in the remaining budget (skipping — not stopping at — ones that
    /// don't, so a later, cheaper item can still fill the gap).
    /// </summary>
    private static (IReadOnlyList<T> Selected, int TokensUsed, bool Truncated, int MoreAvailable) GreedyKnapsack<T>(
        IReadOnlyList<T> items, Func<T, double> score, Func<T, int> tokenCost, int budget)
    {
        var ordered = items
            .Select(item => (Item: item, Cost: Math.Max(1, tokenCost(item)), Score: score(item)))
            .OrderByDescending(x => x.Score / x.Cost)
            .ToList();

        var selected = new List<T>();
        var used = 0;

        foreach (var x in ordered)
        {
            if (used + x.Cost > budget)
                continue;

            selected.Add(x.Item);
            used += x.Cost;
        }

        var truncated = selected.Count < items.Count;
        return (selected, used, truncated, items.Count - selected.Count);
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
