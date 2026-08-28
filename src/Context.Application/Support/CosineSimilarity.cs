namespace Context.Application.Support;

/// <summary>
/// Cosine similarity helper used by the brute-force vector search adapter
/// (<see cref="Context.Application.Adapters.SqliteSearchIndex"/>). Context.Memory has its own
/// internal (non-public) copy of the same primitive — duplicated here rather than shared since
/// neither project depends on the other and the function is a five-line pure computation.
/// </summary>
public static class CosineSimilarity
{
    /// <summary>Cosine similarity in [-1, 1]. Returns 0 for a zero-length or dimension-mismatched vector.</summary>
    public static double Compute(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA <= 1e-12 || normB <= 1e-12)
        {
            return 0.0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
