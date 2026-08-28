using System.Security.Cryptography;
using System.Text;
using Context.Providers.Abstractions;

namespace Context.Providers.Embeddings;

/// <summary>
/// Fully offline, zero-external-call embedding provider. Produces a deterministic
/// pseudo-embedding by hashing the input text into a seeded PRNG and normalizing the
/// resulting vector to unit length. This is the default provider so the whole retrieval
/// pipeline runs end-to-end without any API keys or native binaries.
///
/// Determinism: the same input text always yields the exact same vector (same seed),
/// which is required for stable Embedding.ContentHash-keyed caching (see CONTRACTS.md §1).
/// </summary>
public sealed class NoOpEmbeddingProvider : IEmbeddingProvider
{
    public const int VectorDimensions = 256;

    public string Name => "noop";

    public int Dimensions => VectorDimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ComputeEmbedding(text));
    }

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            result[i] = ComputeEmbedding(texts[i]);
        }

        return Task.FromResult(result);
    }

    private static float[] ComputeEmbedding(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        // Derive a deterministic 32-bit seed from the first 4 bytes of the content hash.
        var seed = BitConverter.ToInt32(hash, 0);
        var rng = new Random(seed);

        var vector = new float[VectorDimensions];
        var sumOfSquares = 0.0;
        for (var i = 0; i < VectorDimensions; i++)
        {
            var component = rng.NextDouble() * 2.0 - 1.0; // uniform in [-1, 1)
            vector[i] = (float)component;
            sumOfSquares += component * component;
        }

        var norm = Math.Sqrt(sumOfSquares);
        if (norm > 1e-12)
        {
            for (var i = 0; i < VectorDimensions; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }
}
