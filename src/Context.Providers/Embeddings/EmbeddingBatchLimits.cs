namespace Context.Providers.Embeddings;

/// <summary>
/// How much one embedding request may carry: at most <see cref="MaxInputs"/> texts totalling at most
/// <see cref="MaxChars"/> characters (a single longer text still goes alone). Exposed by
/// <see cref="BatchingEmbeddingGenerator"/>, which enforces it on every call.
/// </summary>
public sealed record EmbeddingBatchLimits(int MaxInputs, int MaxChars)
{
    /// <summary>Upper bound for the per-project <c>EmbeddingBatchSize</c> override.</summary>
    public const int MaxConfigurableInputs = 256;

    public static readonly EmbeddingBatchLimits None = new(1, int.MaxValue);

    /// <summary>
    /// Every provider here sends a multi-text call as one request with an <c>input</c> array:
    /// OllamaSharp posts all values to <c>/api/embed</c>, the OpenAI SDK to <c>/embeddings</c>, and
    /// <see cref="FoundryLocalEmbeddingGenerator"/> to Foundry Local's <c>/v1/embeddings</c>.
    /// </summary>
    public static EmbeddingBatchLimits ForProvider(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        // The char cap keeps one CPU-bound request well inside the request timeout.
        "ollama" => new(16, 32_000),
        // OpenAI allows 2048 inputs and 300k tokens per request; 250k chars stays under the token cap
        // even at one token per character.
        "openai" => new(100, 250_000),
        // Foundry Local documents batch input from 1.1, but no server was available to verify it here.
        "foundry" => None,
        _ => new(MaxConfigurableInputs, int.MaxValue),
    };

    /// <summary>Replaces <see cref="MaxInputs"/> when <paramref name="maxInputs"/> is positive; 0 keeps the provider default.</summary>
    public EmbeddingBatchLimits WithMaxInputs(int maxInputs) => maxInputs > 0 ? this with { MaxInputs = maxInputs } : this;

    /// <summary>Splits <paramref name="items"/>, in order, into request-sized batches.</summary>
    public IEnumerable<IReadOnlyList<T>> Split<T>(IEnumerable<T> items, Func<T, string> text)
    {
        var batch = new List<T>();
        long chars = 0;
        foreach (var item in items)
        {
            var length = text(item).Length;
            if (batch.Count > 0 && (batch.Count >= MaxInputs || chars + length > MaxChars))
            {
                yield return batch;
                batch = [];
                chars = 0;
            }

            batch.Add(item);
            chars += length;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
