using Context.Indexing.Abstractions;
using Context.Shared;

namespace Context.Indexing;

/// <summary>Result of diffing two symbol-draft snapshots of the same file, by <c>QualifiedName</c>.</summary>
public sealed record SymbolDiffResult(
    IReadOnlyList<SymbolDraft> Added,
    IReadOnlyList<SymbolDraft> Removed,
    IReadOnlyList<SymbolDraft> Changed,
    IReadOnlyList<SymbolDraft> Unchanged);

/// <summary>
/// Symbol-level diffing between a file's previously-extracted symbols and its newly-extracted
/// ones, matched by <c>QualifiedName</c> and compared by a hash of <see cref="CombinedText"/>.
/// This is what lets reindexing be symbol-level rather than whole-file: only
/// <c>Added</c>/<c>Changed</c> symbols need new embeddings/graph edges recomputed,
/// <c>Removed</c> ones need their graph nodes invalidated, and <c>Unchanged</c> ones can be
/// skipped entirely.
/// </summary>
public static class SymbolDiffer
{
    /// <summary>
    /// The text a symbol is diffed AND chunked/embedded on — shared by
    /// <see cref="Diff"/> and <c>IndexingPipeline</c>'s chunk-text construction so the two always
    /// agree on what "changed" means. A parser reports an empty <c>BodyText</c> for a bodyless
    /// declaration (an interface member, an abstract method, an expression-bodied member folded
    /// into <c>SignatureText</c> — see e.g. TreeSitterCSharpParser's own documented fallback for
    /// that case); leading with the signature means those are diffed/chunked on their signature
    /// instead of an empty string.
    /// </summary>
    public static string CombinedText(string? signatureText, string? bodyText)
    {
        var signature = signatureText?.Trim() ?? string.Empty;
        var body = bodyText?.Trim() ?? string.Empty;

        if (body.Length == 0)
        {
            return signature;
        }

        return signature.Length == 0 ? body : $"{signature}\n{body}";
    }

    public static SymbolDiffResult Diff(IReadOnlyList<SymbolDraft> previous, IReadOnlyList<SymbolDraft> current)
    {
        var previousByQualifiedName = ToLookup(previous);
        var currentByQualifiedName = ToLookup(current);

        var added = new List<SymbolDraft>();
        var changed = new List<SymbolDraft>();
        var unchanged = new List<SymbolDraft>();

        foreach (var (qualifiedName, currentSymbol) in currentByQualifiedName)
        {
            if (!previousByQualifiedName.TryGetValue(qualifiedName, out var previousSymbol))
            {
                added.Add(currentSymbol);
                continue;
            }

            var previousHash = Hashing.HashText(CombinedText(previousSymbol.SignatureText, previousSymbol.BodyText));
            var currentHash = Hashing.HashText(CombinedText(currentSymbol.SignatureText, currentSymbol.BodyText));
            (previousHash == currentHash ? unchanged : changed).Add(currentSymbol);
        }

        var removed = previousByQualifiedName
            .Where(kv => !currentByQualifiedName.ContainsKey(kv.Key))
            .Select(kv => kv.Value)
            .ToList();

        return new SymbolDiffResult(added, removed, changed, unchanged);
    }

    private static Dictionary<string, SymbolDraft> ToLookup(IReadOnlyList<SymbolDraft> symbols)
    {
        var dict = new Dictionary<string, SymbolDraft>(symbols.Count, StringComparer.Ordinal);
        foreach (var symbol in symbols)
            dict[symbol.QualifiedName] = symbol; // last-wins if a file yields a duplicate qualified name
        return dict;
    }
}
