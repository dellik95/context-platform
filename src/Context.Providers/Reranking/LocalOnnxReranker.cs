using System.Text.RegularExpressions;
using Context.Providers.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;

namespace Context.Providers.Reranking;

/// <summary>
/// Default reranker. Provides a clean seam for a real ONNX cross-encoder model via
/// Microsoft.ML.OnnxRuntime: if <see cref="LocalOnnxRerankerOptions.ModelPath"/> points at an
/// existing file, an <see cref="InferenceSession"/> is loaded for future use. No reranker model
/// ships in this repository, and running a cross-encoder correctly also requires knowing its
/// exact tokenizer/tensor contract, so actual scoring always uses a lightweight, dependency-free
/// lexical token-overlap heuristic today. This is logged clearly so the simplification is visible
/// at runtime. Swapping in real inference (tokenize -> run session -> read logits) is the
/// intended follow-up once a concrete model is chosen.
/// </summary>
public sealed class LocalOnnxReranker : IRerankerProvider, IDisposable
{
    private static readonly Regex TokenPattern = new(@"[A-Za-z0-9_]+", RegexOptions.Compiled);

    private readonly LocalOnnxRerankerOptions _options;
    private readonly ILogger<LocalOnnxReranker> _logger;
    private readonly InferenceSession? _session;

    public LocalOnnxReranker(LocalOnnxRerankerOptions? options = null, ILogger<LocalOnnxReranker>? logger = null)
    {
        _options = options ?? new LocalOnnxRerankerOptions();
        _logger = logger ?? NullLogger<LocalOnnxReranker>.Instance;

        if (string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            _logger.LogInformation(
                "LocalOnnxReranker: no ONNX model path configured. Using the lexical token-overlap heuristic reranker.");
            return;
        }

        if (!File.Exists(_options.ModelPath))
        {
            _logger.LogWarning(
                "LocalOnnxReranker: configured ONNX model path '{ModelPath}' was not found. " +
                "Falling back to the lexical token-overlap heuristic reranker.",
                _options.ModelPath);
            return;
        }

        try
        {
            _session = new InferenceSession(_options.ModelPath);
            _logger.LogWarning(
                "LocalOnnxReranker: loaded ONNX model from '{ModelPath}', but real cross-encoder " +
                "inference (tokenization + tensor I/O) is not wired up yet in this build. " +
                "Still using the lexical token-overlap heuristic reranker for scoring.",
                _options.ModelPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "LocalOnnxReranker: failed to load ONNX model from '{ModelPath}'. " +
                "Falling back to the lexical token-overlap heuristic reranker.",
                _options.ModelPath);
            _session = null;
        }
    }

    public string Name => "local-onnx";

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

    public void Dispose() => _session?.Dispose();
}
