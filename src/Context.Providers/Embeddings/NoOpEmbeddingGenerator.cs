using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Context.Providers.Embeddings;

/// <summary>
/// Deterministic pseudo-embedding (hash text into a seeded PRNG, normalize to unit length) so
/// the retrieval pipeline runs end-to-end with no API keys or native binaries. Same input always
/// yields the same vector -- required for Embedding.ContentHash-keyed caching.
/// </summary>
public sealed class NoOpEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int VectorDimensions = 256;

    private static readonly EmbeddingGeneratorMetadata Metadata = new("noop", null, "noop", VectorDimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var embeddings = values.Select(text => new Embedding<float>(ComputeEmbedding(text)));
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : null;

    public void Dispose()
    {
    }

    private static float[] ComputeEmbedding(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
        var seed = BitConverter.ToInt32(hash, 0);
        var rng = new Random(seed);

        var vector = new float[VectorDimensions];
        var sumOfSquares = 0.0;
        for (var i = 0; i < VectorDimensions; i++)
        {
            var component = rng.NextDouble() * 2.0 - 1.0;
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
