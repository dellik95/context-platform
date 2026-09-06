using Context.Indexing.Abstractions;

namespace Context.Application.Indexing;

/// <summary>
/// DI default for <see cref="IFileSummarizer"/> so indexing always has a working (possibly no-op)
/// summarizer to call. Mirrors <see cref="NullIndexingProgressReporter"/>'s own private-constructor/
/// static-Instance shape.
/// </summary>
public sealed class NullFileSummarizer : IFileSummarizer
{
    public static readonly NullFileSummarizer Instance = new();

    private NullFileSummarizer()
    {
    }

    public Task<string?> SummarizeAsync(Guid projectId, string relativePath, IReadOnlyList<SymbolDraft> symbols, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
