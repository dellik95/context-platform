namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Real tree-sitter-backed <see cref="Context.Indexing.Abstractions.ILanguageParser"/> for plain
/// TypeScript (grammar id <c>"typescript"</c>, extension <c>.ts</c> only - <c>.tsx</c> is
/// <see cref="TreeSitterTsxParser"/>, since <c>Parse</c> takes no filename and the two grammars
/// differ, e.g. a bare <c>&lt;T&gt;</c> generic-arrow-function cast is ambiguous with JSX and the
/// "tsx" grammar resolves it differently than "typescript" does). All extraction logic lives in
/// <see cref="TreeSitterTypeScriptLikeParserBase"/>, shared with <see cref="TreeSitterTsxParser"/>.
/// </summary>
public sealed class TreeSitterTypeScriptParser() : TreeSitterTypeScriptLikeParserBase("typescript")
{
    public override IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts" };
}
