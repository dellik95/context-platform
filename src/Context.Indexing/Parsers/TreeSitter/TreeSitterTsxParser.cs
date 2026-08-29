namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Real tree-sitter-backed <see cref="Context.Indexing.Abstractions.ILanguageParser"/> for TSX
/// (grammar id <c>"tsx"</c>, extension <c>.tsx</c> only). Verified empirically that the "tsx"
/// grammar's node/field shapes for classes/interfaces/methods/fields/heritage are identical to
/// plain "typescript" (both are the TypeScript grammar with JSX parsing enabled) - so this reuses
/// every query fragment from <see cref="TreeSitterTypeScriptLikeParserBase"/> unchanged, differing
/// only in the <see cref="Context.Indexing.Abstractions.ILanguageParser.SupportedExtensions"/> and
/// the tree-sitter grammar id passed to the base constructor.
/// </summary>
public sealed class TreeSitterTsxParser() : TreeSitterTypeScriptLikeParserBase("tsx")
{
    public override IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tsx" };
}
