using Context.Memory.Extraction;

namespace Context.Memory.Consolidation;

/// <summary>The text a memory is embedded from; every writer must use the same form or stored vectors
/// stop being comparable with fresh ones.</summary>
public static class MemoryEmbeddingText
{
    public static string For(Context.Domain.Entities.Memory memory) => For(memory.Title, memory.Body);

    public static string For(MemoryCandidate candidate) => For(candidate.Title, candidate.Body);

    /// <summary>Beyond this the tail of a merged memory is not embedded: a provider rejects or silently cuts
    /// an input past its context window, and the opening of the body carries the subject.</summary>
    public const int MaxBodyChars = 24_000;

    public static string For(string title, string body) =>
        $"{title}\n{(body.Length <= MaxBodyChars ? body : body[..MaxBodyChars])}";
}
