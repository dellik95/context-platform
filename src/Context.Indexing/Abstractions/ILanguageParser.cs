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

    /// <param name="projectSymbolNames">
    /// Names of every symbol already indexed elsewhere in the project (not just this file) --
    /// without this, a call whose callee is declared in a different file (the overwhelming
    /// majority of real calls, e.g. anything reached through an injected interface) can't be told
    /// apart from a call into third-party/framework code with no corresponding project symbol at
    /// all, and gets silently dropped rather than flooding the graph with an UnresolvedExternal
    /// node per BCL/framework call. Null/omitted is treated as empty (this file's own declarations only).
    /// </param>
    IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast, IReadOnlySet<string>? projectSymbolNames = null);
}
