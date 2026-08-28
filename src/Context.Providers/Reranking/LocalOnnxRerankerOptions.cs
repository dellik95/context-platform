namespace Context.Providers.Reranking;

/// <summary>Configuration for <see cref="LocalOnnxReranker"/>.</summary>
public sealed record LocalOnnxRerankerOptions
{
    /// <summary>
    /// Absolute path to a local ONNX cross-encoder reranker model. When null/empty or when the
    /// file does not exist, <see cref="LocalOnnxReranker"/> falls back to a lexical
    /// token-overlap heuristic so the pipeline keeps working with no shipped model.
    /// </summary>
    public string? ModelPath { get; init; }
}
