using System.Linq;
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
/// ones, matched by <c>QualifiedName</c> and compared by a hash of <c>BodyText</c>. This is what
/// lets reindexing be symbol-level rather than whole-file: only <c>Added</c>/<c>Changed</c>
/// symbols need new embeddings/graph edges recomputed, <c>Removed</c> ones need their graph nodes
/// invalidated, and <c>Unchanged</c> ones can be skipped entirely.
/// </summary>
public static class SymbolDiffer
{
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

            var previousHash = Hashing.HashText(previousSymbol.BodyText);
            var currentHash = Hashing.HashText(currentSymbol.BodyText);
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
