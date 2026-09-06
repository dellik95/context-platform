using Context.Indexing.Abstractions;

namespace Context.Application.Indexing;

public interface IFileSummarizer
{
    // relativePath for context in the prompt; symbols is the file's top-level parsed symbols
    // (name, kind, signature, doc comment -- enough signal for a real description of the file's
    // methods/responsibilities without resending the whole file's source text/body).
    // Returns null if summarization isn't possible/worthwhile (e.g. offline/no-op LLM, or the
    // file has no symbols at all) -- caller must treat null as "skip", not an error.
    Task<string?> SummarizeAsync(Guid projectId, string relativePath, IReadOnlyList<SymbolDraft> symbols, CancellationToken ct);
}
