namespace Context.Indexing.Abstractions;

/// <summary>
/// Language-specific AST parser seam. A real implementation would wrap a tree-sitter grammar;
/// <c>RegexHeuristicParser</c> is the MVP substitute shipped by default (see its header comment).
/// Callers select an implementation by checking <see cref="SupportedExtensions"/> against the
/// file's extension — <see cref="Parse"/> itself takes no filename, so an instance must only
/// ever be handed source text for extensions it declared support for. See CONTRACTS.md §3.
/// </summary>
public interface ILanguageParser
{
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <param name="previousTree">
    /// Optional previously-parsed tree for the same file, to support incremental re-parse.
    /// A real tree-sitter parser would use this for incremental edits; the regex-heuristic
    /// MVP ignores it and always re-parses from scratch.
    /// </param>
    ParsedAst Parse(string sourceText, ParsedAst? previousTree = null);

    IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast);

    IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast);
}
