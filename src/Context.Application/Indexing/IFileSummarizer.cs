namespace Context.Application.Indexing;

public interface IFileSummarizer
{
    // relativePath for context in the prompt; symbolNames is the file's top-level symbol names
    // (enough signal for a short summary without resending the whole file's source text).
    // Returns null if summarization isn't possible/worthwhile (e.g. offline/no-op LLM, or the
    // file has no symbols at all) -- caller must treat null as "skip", not an error.
    Task<string?> SummarizeAsync(string relativePath, IReadOnlyList<string> symbolNames, CancellationToken ct);
}
