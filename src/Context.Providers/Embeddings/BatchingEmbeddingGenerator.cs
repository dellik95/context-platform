using Microsoft.Extensions.AI;

namespace Context.Providers.Embeddings;

/// <summary>
/// Outermost decorator on every per-project embedding generator: splits a call into requests within
/// <see cref="EmbeddingBatchLimits"/> and exposes those limits through <see cref="GetService"/>, so a
/// caller that plans its own batches (<c>EmbedChunkJobHandler</c>) can make each call one request.
/// </summary>
public sealed class BatchingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, EmbeddingBatchLimits limits)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values as IReadOnlyList<string> ?? [.. values];
        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        foreach (var batch in limits.Split(inputs, text => text))
        {
            GeneratedEmbeddings<Embedding<float>> part;
            try
            {
                part = await base.GenerateAsync(batch, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or EmbeddingContextLengthException)
                && EmbeddingContextLengthException.IsContextLengthError(ex))
            {
                throw new EmbeddingContextLengthException(
                    $"The embedding provider rejected this content as exceeding the model's context window: {ex.Message}", ex);
            }

            // Vectors are matched to inputs by position, so a short response would store them against the wrong content.
            if (part.Count != batch.Count)
            {
                throw new InvalidOperationException($"The embedding provider returned {part.Count} embeddings for {batch.Count} inputs.");
            }

            result.AddRange(part);
            if (part.Usage is { } usage)
            {
                (result.Usage ??= new UsageDetails()).Add(usage);
            }
        }

        return result;
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(EmbeddingBatchLimits) ? limits : base.GetService(serviceType, serviceKey);
}
