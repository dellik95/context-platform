using Context.Indexing.Abstractions;
using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Real tree-sitter-backed <see cref="ILanguageParser"/> for C# (grammar id <c>"c-sharp"</c>).
/// Extracts classes/interfaces/structs/enums/records/methods/constructors/properties/fields and a
/// best-effort Calls/Extends/Implements/References/Tests/Imports relation set, all
/// unresolved-by-name per <see cref="RelationDraft"/>'s contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime:</b> the <see cref="TS.Language"/> is created once in the constructor and kept for
/// this instance's entire lifetime (parsers are registered as DI singletons). Each <see cref="Parse"/>
/// call creates a fresh <see cref="TS.Parser"/>/<see cref="TS.Tree"/> pair, boxed into a
/// <see cref="TreeSitterParsedTree"/> — both are disposed as the last step of whichever of
/// <see cref="ExtractSymbols"/>/<see cref="ExtractRelations"/> runs on that <see cref="ParsedAst"/>
/// (never both — see <see cref="TreeSitterParsedTree"/>'s remarks). This type itself implements
/// <see cref="IDisposable"/> purely so the DI container's <c>ServiceProvider</c> disposes the
/// long-lived <see cref="TS.Language"/> on shutdown; <see cref="ILanguageParser"/> itself has no
/// such requirement.
/// </para>
/// <para>
/// <b><c>previousTree</c>:</b> intentionally ignored — every real caller discards a
/// <see cref="ParsedAst"/> immediately after its single extract call, so always re-parses from
/// scratch.
/// </para>
/// </remarks>
public sealed partial class TreeSitterCSharpParser : ILanguageParser, IDisposable
{
    private const string DocLinePrefix = "///";

    private static readonly HashSet<string> TypeDeclarationTypes = new(StringComparer.Ordinal)
    {
        "class_declaration", "interface_declaration", "struct_declaration", "enum_declaration",
        "record_declaration", "record_struct_declaration"
    };

    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod", "Property"
    };

    private readonly TS.Language _language = new("c-sharp");
    private readonly bool _supportsRecordStructNode;
    private bool _disposed;

    public TreeSitterCSharpParser()
    {
        try
        {
            using var probe = new TS.Query(_language, RecordStructSymbolsQuery);
            _supportsRecordStructNode = true;
        }
        catch (Exception)
        {
            _supportsRecordStructNode = false;
        }
    }

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };

    public ParsedAst Parse(string sourceText, ParsedAst? previousTree = null)
    {
        // previousTree is intentionally unused — see class remarks.
        var parser = new TS.Parser(_language);
        var tree = parser.Parse(sourceText);
        if (tree is null)
        {
            parser.Dispose();
            throw new InvalidOperationException("tree-sitter produced a null Tree for C# source.");
        }

        return new ParsedAst(new TreeSitterParsedTree(parser, tree), sourceText);
    }

    private static TreeSitterParsedTree RequireTree(ParsedAst ast) =>
        ast.Tree as TreeSitterParsedTree
        ?? throw new ArgumentException(
            $"ParsedAst.Tree was not produced by {nameof(TreeSitterCSharpParser)}.", nameof(ast));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _language.Dispose();
    }
}
