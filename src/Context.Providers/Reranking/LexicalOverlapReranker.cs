using System.Text.RegularExpressions;
using Context.Providers.Abstractions;

namespace Context.Providers.Reranking;

/// <summary>
/// Default reranker. Reorders candidates by a lightweight, dependency-free lexical token-overlap
/// heuristic (query/candidate token Jaccard-style overlap) -- there is no machine-learned or
/// cross-encoder scoring here, despite this being the reranking stage's default implementation.
/// </summary>
/// <remarks>
/// This type previously loaded a <c>Microsoft.ML.OnnxRuntime.InferenceSession</c> as a "seam" for
/// a future ONNX cross-encoder model, but that session was never actually used to score or
/// reorder candidates -- scoring always fell through to this same heuristic. No reranker model
/// ships with this project (or is referenced as a downloadable asset anywhere in the repo), and
/// the model-path configuration that would have activated that code path was never wired up to
/// any configuration source, so the ONNX loading branch was unreachable dead code that merely
/// implied ML-based reranking was happening. It was removed rather than kept as a scaffold: a real
/// cross-encoder integration needs a tokenizer/tensor contract specific to whichever model is
/// eventually chosen, so an inert <c>InferenceSession</c> load did not meaningfully reduce the
/// future work, while it did actively mislead readers about current behavior. If/when a model is
/// adopted, add the ONNX dependency and inference code back then, alongside the model itself.
/// </remarks>
public sealed class LexicalOverlapReranker : IRerankerProvider
{
    private static readonly Regex TokenPattern = new(@"[A-Za-z0-9_]+", RegexOptions.Compiled);

    public string Name => "lexical-overlap";

    public Task<IReadOnlyList<int>> RerankAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<int> ranked = HeuristicRerank(query, candidates);
        return Task.FromResult(ranked);
    }

    private static IReadOnlyList<int> HeuristicRerank(string query, IReadOnlyList<string> candidates)
    {
        var queryTokens = Tokenize(query);
        var scored = new (int Index, double Score)[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            scored[i] = (i, TokenOverlapScore(queryTokens, Tokenize(candidates[i])));
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index).Select(s => s.Index)];
    }

    private static double TokenOverlapScore(HashSet<string> queryTokens, HashSet<string> candidateTokens)
    {
        if (queryTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0.0;
        }

        var overlap = queryTokens.Count(candidateTokens.Contains);
        return (double)overlap / queryTokens.Count;
    }

    private static HashSet<string> Tokenize(string? text) =>
        [.. TokenPattern.Matches(text ?? string.Empty).Select(m => m.Value.ToLowerInvariant())];
}
