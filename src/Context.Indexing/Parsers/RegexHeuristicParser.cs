using Context.Indexing.Abstractions;

namespace Context.Indexing.Parsers;

/// <summary>
/// Which curly-brace-or-indentation "dialect" of regex/line heuristics an instance applies.
/// TypeScript and JavaScript (plus their JSX/TSX variants) share one dialect because their
/// declaration syntax is close enough for this MVP's purposes.
/// </summary>
public enum CodeDialect
{
    TypeScriptFamily,
    CSharp,
    Python,
}

/// <summary>
/// <para>
/// INTENTIONAL MVP SUBSTITUTE for a real tree-sitter grammar. This is <b>not</b> a real parser —
/// it recognizes class/interface/struct/enum/function/method declarations and their approximate
/// body ranges using per-line regexes plus brace/indentation counting, with comments and string
/// literals blanked out first so their contents can't be mistaken for code structure. It gets the
/// common cases right and can be fooled by unusual formatting, macros, multi-line generic
/// constraints, etc. — acceptable for an MVP that must run with zero native dependencies.
/// </para>
/// <para>
/// It implements <see cref="ILanguageParser"/> like any other implementation would, so a real
/// tree-sitter-backed parser can later be registered in its place (see
/// <c>ServiceCollectionExtensions.AddContextIndexing</c>) without any downstream caller — file
/// scanning, symbol diffing, graph building — needing to change.
/// </para>
/// <para>
/// One instance covers exactly one <see cref="CodeDialect"/> and therefore one fixed set of file
/// extensions: <see cref="ILanguageParser.Parse"/> takes no filename, so the pipeline must pick
/// the right registered <see cref="ILanguageParser"/> via <see cref="SupportedExtensions"/> before
/// calling it (a plain <c>.cs</c> parser is never handed Python source, etc.).
/// </para>
/// </summary>
public sealed class RegexHeuristicParser : ILanguageParser
{
    private readonly CodeDialect _dialect;

    public RegexHeuristicParser(CodeDialect dialect)
    {
        _dialect = dialect;
        SupportedExtensions = dialect switch
        {
            CodeDialect.TypeScriptFamily => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".js", ".jsx" },
            CodeDialect.CSharp => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
            CodeDialect.Python => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py" },
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null),
        };
    }

    public IReadOnlySet<string> SupportedExtensions { get; }

    public ParsedAst Parse(string sourceText, ParsedAst? previousTree = null)
    {
        // previousTree is intentionally unused: unlike a real tree-sitter grammar, this MVP
        // has no incremental re-parse capability and always re-derives the tree from scratch.
        var tree = RegexAstTree.Build(_dialect, sourceText);
        return new ParsedAst(tree, sourceText);
    }

    public IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast) => RequireTree(ast).Symbols;

    public IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast) => RequireTree(ast).Relations;

    private static RegexAstTree RequireTree(ParsedAst ast) =>
        ast.Tree as RegexAstTree
        ?? throw new ArgumentException(
            $"ParsedAst.Tree was not produced by {nameof(RegexHeuristicParser)}.", nameof(ast));
}
