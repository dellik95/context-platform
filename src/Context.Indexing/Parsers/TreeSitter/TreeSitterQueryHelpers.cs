using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Small helpers shared across every tree-sitter-backed <see cref="Context.Indexing.Abstractions.ILanguageParser"/>
/// implementation (starting with <see cref="TreeSitterCSharpParser"/>; TypeScript/JavaScript/Python
/// parsers land in a later phase and are expected to reuse these rather than re-derive them).
/// Deliberately minimal — only what's actually shared, not a speculative framework.
/// </summary>
/// <remarks>
/// No hashing helper lives here: <see cref="Context.Indexing.Abstractions.SymbolDraft"/> carries raw
/// <c>SignatureText</c>/<c>BodyText</c>, not hashes — <c>IndexingPipeline</c> is the one place that
/// calls <c>Context.Shared.Hashing</c> (via <c>IContentHasher</c>) to turn those into
/// <c>Symbol.SignatureHash</c>/<c>Symbol.BodyHash</c>. Every <c>ILanguageParser</c> implementation,
/// tree-sitter-backed or not, already shares that convention for free by producing raw text.
/// </remarks>
internal static class TreeSitterQueryHelpers
{
    /// <summary>
    /// Looks up a named field on <paramref name="node"/> without throwing when the grammar doesn't
    /// define that field for this node's production (tree-sitter's <c>Node</c> indexer throws
    /// <see cref="KeyNotFoundException"/> in that case — e.g. an interface member's
    /// <c>method_declaration</c> has no <c>body</c> field at all, vs. a class method where it's
    /// simply absent-but-defined). Safe to call for any field name on any node.
    /// </summary>
    public static TS.Node? TryGetField(TS.Node node, string fieldName)
    {
        foreach (var (name, value) in node.Fields)
        {
            if (name == fieldName)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Joins a nesting prefix and a name into a dotted qualified name, tolerating a null/empty prefix.</summary>
    public static string Combine(string? prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

    /// <summary>
    /// Depth-first search for every descendant named node of the given grammar <paramref name="type"/>
    /// under <paramref name="root"/> (root itself included). Used e.g. to find every
    /// <c>invocation_expression</c>/<c>call_expression</c> inside a method body for best-effort
    /// Calls-relation extraction.
    /// </summary>
    public static IEnumerable<TS.Node> DescendantsOfType(TS.Node root, string type)
    {
        if (root.Type == type)
        {
            yield return root;
        }

        foreach (var child in root.NamedChildren)
        {
            foreach (var descendant in DescendantsOfType(child, type))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Best-effort leading doc-comment for a declaration node, mirroring
    /// <c>RegexHeuristicParser</c>'s convention: only <c>///</c>-style line comments or a
    /// <c>/* ... */</c> block comment count (a plain <c>//</c> line comment does not); the comment
    /// must be the immediately preceding named sibling (blank lines in between are fine — they
    /// produce no nodes — but another statement or declaration in between is not). For a run of
    /// contiguous <c>///</c> lines, every line must be exactly one source row above the next (no
    /// blank line inside the run itself), matching <c>RegexHeuristicParser.ExtractCStyleDocComment</c>.
    /// </summary>
    /// <param name="declNode">The declaration node (e.g. a <c>class_declaration</c> or <c>method_declaration</c>).</param>
    /// <param name="lineCommentPrefix">The doc-comment line marker for this language, e.g. <c>"///"</c>.</param>
    public static string? ExtractLeadingDocComment(TS.Node declNode, string lineCommentPrefix)
    {
        var prev = declNode.PreviousNamedSibling;
        if (prev is null || prev.Type != "comment")
        {
            return null;
        }

        var text = prev.Text;
        var trimmedStart = text.TrimStart();

        if (trimmedStart.StartsWith("/*", StringComparison.Ordinal) && text.TrimEnd().EndsWith("*/", StringComparison.Ordinal))
        {
            return text.Trim();
        }

        if (!trimmedStart.StartsWith(lineCommentPrefix, StringComparison.Ordinal))
        {
            return null; // a plain "//" comment isn't a doc comment
        }

        var lines = new List<string> { StripLinePrefix(text, lineCommentPrefix) };
        var current = prev;
        while (current.PreviousNamedSibling is { Type: "comment" } candidate
               && candidate.Text.TrimStart().StartsWith(lineCommentPrefix, StringComparison.Ordinal)
               && candidate.EndPosition.Row + 1 == current.StartPosition.Row)
        {
            lines.Insert(0, StripLinePrefix(candidate.Text, lineCommentPrefix));
            current = candidate;
        }

        return string.Join('\n', lines);
    }

    private static string StripLinePrefix(string commentText, string prefix)
    {
        var trimmed = commentText.TrimStart();
        return trimmed.StartsWith(prefix, StringComparison.Ordinal) ? trimmed[prefix.Length..].Trim() : trimmed.Trim();
    }
}
