using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Shared tree-sitter-backed <see cref="ILanguageParser"/> logic for the JavaScript/TypeScript
/// grammar family (<see cref="TreeSitterTypeScriptParser"/>, <see cref="TreeSitterTsxParser"/>,
/// <see cref="TreeSitterJavaScriptParser"/>). The interface's own constraint — <c>Parse</c> takes
/// no filename, so one instance must correspond to exactly one grammar — means these are three
/// separate <c>ILanguageParser</c> registrations even though they share almost everything: this
/// base class holds that shared logic, and each subclass supplies only its <see cref="TS.Language"/>
/// id, <see cref="SupportedExtensions"/>, and the handful of grammar-specific query fragments where
/// the three grammars genuinely differ (field declarations, interfaces, class heritage shape).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime:</b> identical to <see cref="TreeSitterCSharpParser"/> — the <see cref="TS.Language"/>
/// is created once in the constructor and kept alive for this instance's whole lifetime (these
/// parsers are registered as DI singletons); each <see cref="Parse"/> call creates a fresh
/// <see cref="TS.Parser"/>/<see cref="TS.Tree"/> pair boxed into a <see cref="TreeSitterParsedTree"/>,
/// disposed as the last step of whichever of <see cref="ExtractSymbols"/>/<see cref="ExtractRelations"/>
/// runs on that <see cref="ParsedAst"/> (never both, per this codebase's actual call sites — see
/// <see cref="TreeSitterParsedTree"/>'s remarks).
/// </para>
/// <para>
/// <b><c>previousTree</c>:</b> intentionally ignored, for the same reason as
/// <see cref="TreeSitterCSharpParser"/> — always re-parses from scratch.
/// </para>
/// </remarks>
public abstract class TreeSitterJsFamilyParserBase : ILanguageParser, IDisposable
{
    // A doc comment in this grammar family is JSDoc-style ("/** ... */"); a plain "//" line
    // comment is never treated as one. TreeSitterQueryHelpers.ExtractLeadingDocComment's
    // line-comment path only fires when a comment starts with the given prefix, so passing a
    // prefix that legitimate JS/TS source will never start a comment with ("///") reuses that
    // helper's block-comment handling as-is while suppressing its line-comment path entirely,
    // with no changes needed to a helper shared with the (nothing-to-do-with-JSDoc) C# parser.
    private const string DocCommentLinePrefix = "///";

    // Every pattern captures the whole declaration as "<prefix>.def" and its name identifier as
    // "<prefix>.name", exactly like TreeSitterCSharpParser's SymbolsQuery, so ExtractSymbols can
    // handle every kind uniformly with one query execution + one dictionary lookup per match.
    // "name: (_)" (wildcard) covers both TS's `type_identifier` and plain JS's `identifier` class
    // names without needing two separate patterns.
    private const string CommonSymbolPatterns = """
        (class_declaration name: (_) @class.name) @class.def
        (method_definition name: (property_identifier) @method.name) @method.def
        (function_declaration name: (identifier) @function.name) @function.def
        (variable_declarator name: (identifier) @varfn.name value: (arrow_function) @varfn.value) @varfn.def
        (variable_declarator name: (identifier) @varfn.name value: (function_expression) @varfn.value) @varfn.def
        """;

    // (string (string_fragment) @import.name) captures the bare module specifier text with the
    // surrounding quotes already stripped by the grammar itself - no manual trimming needed.
    private const string ImportsQuery = "(import_statement source: (string (string_fragment) @import.name))";

    // Two shapes for a real invocation, mirroring TreeSitterCSharpParser's InvocationsQuery: a bare
    // `foo()` call, and a member-expression call (`this.foo()`/`obj.foo()`/every chained call like
    // `items.map(...)`) — verified against a real parse tree: `x.foo()` is a call_expression whose
    // function is a member_expression with object:"x" property:"foo" (a property_identifier, not a
    // plain identifier — that's the field this pattern targets).
    private const string CallsQuery = """
        (call_expression function: (identifier) @call.callee) @call.expr
        (call_expression function: (member_expression property: (property_identifier) @call.callee)) @call.expr
        """;

    // A function/method named but never actually invoked — passed as a bare-identifier call
    // argument (`items.map(someFunc)`). Verified against a real parse tree: unlike C#'s grammar,
    // JS/TS's `arguments` node wraps each argument expression directly with no per-argument
    // wrapper node, so the pattern targets the identifier itself. Anchored to `call_expression`'s
    // own `arguments` field (not a bare `(arguments (identifier))`) so this can't stray into some
    // other node type that happens to also be named "arguments" in a future grammar revision.
    // Deliberately NOT covered: a plain assignment (`obj.onclick = handler`) — unlike C#'s `+=`/`-=`
    // event-subscription shape, JS/TS has no distinct operator marking "this is a handler wire-up"
    // vs. an ordinary data assignment, so trying to catch it here would risk misreading unrelated
    // assignments as calls; left out rather than guessed at.
    private const string MethodGroupReferencesQuery =
        "(call_expression arguments: (arguments (identifier) @ref.name))";

    private static readonly HashSet<string> TypeContainerTypes =
        new(StringComparer.Ordinal) { "class_declaration", "interface_declaration" };

    private static readonly HashSet<string> NamedFunctionContainerTypes =
        new(StringComparer.Ordinal) { "method_definition", "function_declaration" };

    private static readonly HashSet<string> CallEnclosingTypes =
        new(StringComparer.Ordinal) { "method_definition", "function_declaration", "arrow_function", "function_expression" };

    private readonly TS.Language _language;
    private bool _disposed;

    protected TreeSitterJsFamilyParserBase(string languageId)
    {
        _language = new TS.Language(languageId);
    }

    public abstract IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Query fragment for this grammar's field-declaration node, capturing the whole declaration as
    /// <c>@field.def</c> and its name as <c>@field.name</c>. Differs because plain JS's grammar node
    /// (<c>field_definition</c>, field <c>property</c>) is genuinely a different node/field name than
    /// TS/TSX's (<c>public_field_definition</c>, field <c>name</c>) - not merely a naming choice this
    /// base class could paper over.
    /// </summary>
    protected abstract string FieldQueryFragment { get; }

    /// <summary>
    /// Query fragment for this grammar's interface declaration, or <see cref="string.Empty"/> for
    /// plain JavaScript, which has no interfaces at all - <see cref="TS.Query"/> construction throws
    /// if a pattern names a node type the grammar doesn't define, so this must be omitted entirely
    /// rather than left to match nothing.
    /// </summary>
    protected virtual string InterfaceQueryFragment => string.Empty;

    /// <summary>
    /// Query capturing a class's <c>extends</c> target as <c>@class.extends</c> (with <c>@class.name</c>
    /// / <c>@class.def</c> for the enclosing class), shaped differently per grammar: TS/TSX wrap it in
    /// an <c>extends_clause</c> with a <c>value</c> field, while plain JS's <c>class_heritage</c>
    /// contains the extended expression directly with no wrapper node at all.
    /// </summary>
    protected abstract string ClassExtendsQuery { get; }

    /// <summary>
    /// Query capturing a class's whole <c>implements_clause</c> node as <c>@class.implements</c> (its
    /// individual interface names are then read off its <see cref="TS.Node.NamedChildren"/> in code,
    /// exactly like <c>TreeSitterCSharpParser.BaseListQuery</c> does for <c>base_list</c>), or
    /// <see langword="null"/> for plain JS, which has no <c>implements</c> keyword at all.
    /// </summary>
    protected virtual string? ClassImplementsQuery => null;

    /// <summary>
    /// Query capturing an interface's whole <c>extends_type_clause</c> node as <c>@interface.extends</c>
    /// (individual base interfaces read off its children in code, same convention as
    /// <see cref="ClassImplementsQuery"/>), or <see langword="null"/> for plain JS, which has no
    /// interfaces.
    /// </summary>
    protected virtual string? InterfaceExtendsQuery => null;

    private string SymbolsQuery => InterfaceQueryFragment.Length == 0
        ? CommonSymbolPatterns + FieldQueryFragment
        : CommonSymbolPatterns + FieldQueryFragment + "\n" + InterfaceQueryFragment;

    public ParsedAst Parse(string sourceText, ParsedAst? previousTree = null)
    {
        // previousTree is intentionally unused - see class remarks.
        var parser = new TS.Parser(_language);
        var tree = parser.Parse(sourceText);
        if (tree is null)
        {
            parser.Dispose();
            throw new InvalidOperationException($"tree-sitter produced a null Tree for {GetType().Name} source.");
        }

        return new ParsedAst(new TreeSitterParsedTree(parser, tree), sourceText);
    }

    public IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast)
    {
        var wrapper = RequireTree(ast);
        try
        {
            return BuildSymbolDrafts(wrapper.Tree.RootNode);
        }
        finally
        {
            wrapper.Dispose();
        }
    }

    public IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast, IReadOnlySet<string>? projectSymbolNames = null)
    {
        var wrapper = RequireTree(ast);
        try
        {
            var root = wrapper.Tree.RootNode;
            var relations = new List<RelationDraft>();

            // Exports needs to know which declared names actually became a SymbolDraft (and under
            // what qualified name) in this same file - built once and shared by AddExportRelations
            // rather than re-deriving it.
            var symbolDrafts = BuildSymbolDrafts(root);

            AddInheritanceRelations(root, relations);
            AddImportRelations(root, relations);
            AddCallRelations(root, symbolDrafts, relations, projectSymbolNames);
            AddExportRelations(root, symbolDrafts, relations);

            return relations;
        }
        finally
        {
            wrapper.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Symbols
    // ------------------------------------------------------------------

    private List<SymbolDraft> BuildSymbolDrafts(TS.Node root)
    {
        var drafts = new List<SymbolDraft>();

        using var query = new TS.Query(_language, SymbolsQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            var captures = match.Captures.ToDictionary(c => c.Name, c => c.Node);
            var defEntry = captures.FirstOrDefault(kv => kv.Key.EndsWith(".def", StringComparison.Ordinal));
            if (defEntry.Key is null)
            {
                continue;
            }

            var prefix = defEntry.Key[..^".def".Length];
            if (!captures.TryGetValue($"{prefix}.name", out var nameNode))
            {
                continue;
            }

            var kind = prefix switch
            {
                "class" => SymbolKind.Class,
                "interface" => SymbolKind.Interface,
                "method" => SymbolKind.Method,
                "function" => SymbolKind.Function,
                "varfn" => SymbolKind.Function,
                "field" => SymbolKind.Field,
                _ => (SymbolKind?)null,
            };
            if (kind is null)
            {
                continue;
            }

            var defNode = defEntry.Value;
            var name = nameNode.Text;
            var (qualifiedName, parentQualifiedName) = ComputeQualifiedName(defNode, name);

            // A method or field must be nested in a class; if it isn't (unexpected grammar shape -
            // both node types are only ever grammatically valid inside a class_body in practice),
            // there is no sensible ParentSymbolId to hang it off - skip it, matching
            // TreeSitterCSharpParser's equivalent guard for its own member kinds.
            if (kind is SymbolKind.Method or SymbolKind.Field && parentQualifiedName is null)
            {
                continue;
            }

            var bodyNode = FindBodyNode(prefix, defNode);
            string signatureText;
            string bodyText;
            if (bodyNode is not null && defNode.Text.EndsWith(bodyNode.Text, StringComparison.Ordinal))
            {
                signatureText = defNode.Text[..^bodyNode.Text.Length].Trim();
                bodyText = defNode.Text;
            }
            else
            {
                // Headerless case: an interface method signature (no body at all), a field
                // declaration, or a concise arrow body that couldn't be matched textually - the
                // whole declaration is the signature and BodyText is empty, mirroring
                // RegexHeuristicParser's and TreeSitterCSharpParser's convention.
                signatureText = defNode.Text.Trim();
                bodyText = string.Empty;
            }

            var docComment = TreeSitterQueryHelpers.ExtractLeadingDocComment(DocCommentAnchor(defNode), DocCommentLinePrefix);

            drafts.Add(new SymbolDraft(
                name, qualifiedName, kind.Value,
                defNode.StartPosition.Row + 1, defNode.EndPosition.Row + 1,
                signatureText, bodyText, docComment, parentQualifiedName));
        }

        return drafts;
    }

    /// <summary>
    /// A "varfn" (<c>const foo = (...) =&gt; {...}</c>) def node is the <c>variable_declarator</c>,
    /// which has no <c>body</c> field of its own - the body lives one level down, on its
    /// <c>value</c> field (the <c>arrow_function</c>/<c>function_expression</c>). Every other kind's
    /// def node (class/interface/method/function) carries its own <c>body</c> field directly; a
    /// field declaration has none, which is the correct "headerless" case handled by the caller.
    /// </summary>
    private static TS.Node? FindBodyNode(string prefix, TS.Node defNode)
    {
        if (prefix != "varfn")
        {
            return TreeSitterQueryHelpers.TryGetField(defNode, "body");
        }

        var valueNode = TreeSitterQueryHelpers.TryGetField(defNode, "value");
        return valueNode is null ? null : TreeSitterQueryHelpers.TryGetField(valueNode, "body");
    }

    /// <summary>
    /// The node whose leading comment should be treated as this declaration's doc comment. A
    /// declaration's own doc comment precedes the outermost statement it's wrapped in, not
    /// necessarily the declaration node itself:
    /// <c>/** doc */ export class Foo {}</c> has the comment before the <c>export_statement</c>
    /// (the <c>class_declaration</c>'s own <c>PreviousNamedSibling</c> is nothing - it's
    /// <c>export_statement</c>'s first child), and a "varfn" def node is a <c>variable_declarator</c>
    /// one level below the <c>lexical_declaration</c>/<c>variable_declaration</c> statement the
    /// comment actually precedes (which may itself be wrapped in an <c>export_statement</c>).
    /// Confirmed empirically against real parsed output - see probe notes in the implementation
    /// report.
    /// </summary>
    private static TS.Node DocCommentAnchor(TS.Node defNode)
    {
        var anchor = defNode;
        if (anchor.Parent is { Type: "lexical_declaration" or "variable_declaration" } declStatement)
        {
            anchor = declStatement;
        }

        if (anchor.Parent is { Type: "export_statement" } exportStatement)
        {
            anchor = exportStatement;
        }

        return anchor;
    }

    // ------------------------------------------------------------------
    // Extends / Implements
    // ------------------------------------------------------------------

    private void AddInheritanceRelations(TS.Node root, List<RelationDraft> relations)
    {
        AddClassExtends(root, relations);
        if (ClassImplementsQuery is { } implementsQuery)
        {
            AddClassImplements(root, implementsQuery, relations);
        }

        if (InterfaceExtendsQuery is { } interfaceExtendsQuery)
        {
            AddInterfaceExtends(root, interfaceExtendsQuery, relations);
        }
    }

    private void AddClassExtends(TS.Node root, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, ClassExtendsQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            var captures = match.Captures.ToDictionary(c => c.Name, c => c.Node);
            if (!captures.TryGetValue("class.def", out var defNode) ||
                !captures.TryGetValue("class.name", out var nameNode) ||
                !captures.TryGetValue("class.extends", out var extendsNode))
            {
                continue;
            }

            var (qualifiedName, _) = ComputeQualifiedName(defNode, nameNode.Text);
            var baseName = TypeNameOf(extendsNode);
            if (!string.IsNullOrEmpty(baseName))
            {
                relations.Add(new RelationDraft(qualifiedName, baseName, RelationKind.Extends));
            }
        }
    }

    private void AddClassImplements(TS.Node root, string implementsQuery, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, implementsQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            var captures = match.Captures.ToDictionary(c => c.Name, c => c.Node);
            if (!captures.TryGetValue("class.def", out var defNode) ||
                !captures.TryGetValue("class.name", out var nameNode) ||
                !captures.TryGetValue("class.implements", out var implementsNode))
            {
                continue;
            }

            var (qualifiedName, _) = ComputeQualifiedName(defNode, nameNode.Text);
            foreach (var entry in implementsNode.NamedChildren)
            {
                var interfaceName = TypeNameOf(entry);
                if (!string.IsNullOrEmpty(interfaceName))
                {
                    relations.Add(new RelationDraft(qualifiedName, interfaceName, RelationKind.Implements));
                }
            }
        }
    }

    private void AddInterfaceExtends(TS.Node root, string interfaceExtendsQuery, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, interfaceExtendsQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            var captures = match.Captures.ToDictionary(c => c.Name, c => c.Node);
            if (!captures.TryGetValue("interface.def", out var defNode) ||
                !captures.TryGetValue("interface.name", out var nameNode) ||
                !captures.TryGetValue("interface.extends", out var extendsNode))
            {
                continue;
            }

            var (qualifiedName, _) = ComputeQualifiedName(defNode, nameNode.Text);
            foreach (var entry in extendsNode.NamedChildren)
            {
                var baseName = TypeNameOf(entry);
                if (!string.IsNullOrEmpty(baseName))
                {
                    relations.Add(new RelationDraft(qualifiedName, baseName, RelationKind.Extends));
                }
            }
        }
    }

    /// <summary>
    /// A base-type/interface reference is either a plain name (<c>identifier</c>/<c>type_identifier</c>
    /// in TS, or even a <c>member_expression</c> like <c>React.Component</c>) or, when generic type
    /// arguments are present in an <c>implements</c>/interface-<c>extends</c> position (e.g.
    /// <c>implements IFoo&lt;string&gt;</c>), a <c>generic_type</c> wrapping the real name under its
    /// own <c>name</c> field - confirmed empirically that a class's own <c>extends_clause</c> never
    /// wraps this way (its generic arguments live in a sibling <c>type_arguments</c> field instead,
    /// so <paramref name="node"/>'s text is already the bare name there), but <c>implements</c>/
    /// interface-<c>extends</c> entries do wrap, since they have no such sibling field to hold the
    /// type arguments separately.
    /// </summary>
    private static string TypeNameOf(TS.Node node) =>
        node.Type == "generic_type"
            ? TreeSitterQueryHelpers.TryGetField(node, "name")?.Text ?? node.Text
            : node.Text;

    // ------------------------------------------------------------------
    // Imports
    // ------------------------------------------------------------------

    // RelationDraft has no file-level "source" concept (SourceQualifiedName is always a symbol's
    // qualified name), so an import statement's source is the nearest enclosing named container
    // (or "" for one at module top level, the overwhelmingly common case). That name never matches
    // a persisted Symbol row, so UpdateGraphJobHandler's symbol-only source lookup currently drops
    // these relations rather than turning them into graph edges - the same documented, deliberate
    // limitation as TreeSitterCSharpParser.AddImportRelations; closing it needs a File-sourced
    // relation concept upstream that doesn't exist yet.
    private void AddImportRelations(TS.Node root, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, ImportsQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            foreach (var capture in match.Captures)
            {
                var enclosing = GetEnclosingContainerName(capture.Node);
                relations.Add(new RelationDraft(enclosing, capture.Node.Text, RelationKind.Imports));
            }
        }
    }

    private static string GetEnclosingContainerName(TS.Node node) => string.Join('.', CollectContext(node));

    // ------------------------------------------------------------------
    // Exports
    // ------------------------------------------------------------------

    // An `export`-ed name is only turned into a RelationDraft when it corresponds to one of this
    // file's own just-extracted SymbolDrafts (Class/Interface/Function/top-level "varfn" function) -
    // AddRelationEdgesAsync (UpdateGraphJobHandler) requires SourceQualifiedName to match a symbol
    // this file actually yielded, so there is no sensible Source for e.g. `export const PI = 3.14;`
    // (a plain data constant never becomes a SymbolDraft at all) or `export { x } from "./other"`
    // (a re-export naming a symbol declared in a different file, not this one) - both are left
    // unrepresented rather than inventing a synthetic source. The relation is deliberately
    // reflexive (Source and Target both name the exported symbol): CONTRACTS.md's RelationDraft has
    // no separate "module" concept to be the source instead, and recording "this symbol is exported"
    // as a self-edge is the closest honest fit within the existing shape - UpdateGraphJobHandler
    // turns it into a GraphEdge of kind Exports from the symbol's node to itself.
    private static void AddExportRelations(TS.Node root, List<SymbolDraft> symbolDrafts, List<RelationDraft> relations)
    {
        var exportableByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var draft in symbolDrafts)
        {
            if (draft.ParentQualifiedName is null &&
                draft.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Function)
            {
                exportableByName[draft.Name] = draft.QualifiedName;
            }
        }

        if (exportableByName.Count == 0)
        {
            return;
        }

        foreach (var exportStatement in TreeSitterQueryHelpers.DescendantsOfType(root, "export_statement"))
        {
            var declaration = TreeSitterQueryHelpers.TryGetField(exportStatement, "declaration")
                ?? TreeSitterQueryHelpers.TryGetField(exportStatement, "value"); // `export default someIdentifier;`
            if (declaration is not null)
            {
                AddExportsForDeclaration(declaration, exportableByName, relations);
                continue;
            }

            var exportClause = exportStatement.NamedChildren.FirstOrDefault(c => c.Type == "export_clause");
            if (exportClause is null)
            {
                continue; // e.g. a bare re-export `export * from "./other"` with no local name at all
            }

            foreach (var specifier in exportClause.NamedChildren.Where(c => c.Type == "export_specifier"))
            {
                var localName = TreeSitterQueryHelpers.TryGetField(specifier, "name")?.Text;
                if (localName is null || !exportableByName.TryGetValue(localName, out var qualifiedName))
                {
                    continue;
                }

                var exportedAs = TreeSitterQueryHelpers.TryGetField(specifier, "alias")?.Text ?? localName;
                relations.Add(new RelationDraft(qualifiedName, exportedAs, RelationKind.Exports));
            }
        }
    }

    private static void AddExportsForDeclaration(TS.Node declaration, Dictionary<string, string> exportableByName, List<RelationDraft> relations)
    {
        switch (declaration.Type)
        {
            case "class_declaration" or "interface_declaration" or "function_declaration":
                AddExportIfKnown(TreeSitterQueryHelpers.TryGetField(declaration, "name")?.Text, exportableByName, relations);
                break;

            case "lexical_declaration" or "variable_declaration":
                foreach (var declarator in TreeSitterQueryHelpers.DescendantsOfType(declaration, "variable_declarator"))
                {
                    AddExportIfKnown(TreeSitterQueryHelpers.TryGetField(declarator, "name")?.Text, exportableByName, relations);
                }

                break;

            case "identifier":
                // `export default Foo;` - a bare reference to a symbol declared elsewhere in this
                // same file, rather than a fresh declaration.
                AddExportIfKnown(declaration.Text, exportableByName, relations);
                break;

            default:
                // An anonymous default export (`export default function() {}` / `export default
                // class {}` / `export default () => {}` / `export default { ... }`) has no name to
                // associate a symbol or relation with - left unrepresented rather than invented.
                break;
        }
    }

    private static void AddExportIfKnown(string? name, Dictionary<string, string> exportableByName, List<RelationDraft> relations)
    {
        if (name is not null && exportableByName.TryGetValue(name, out var qualifiedName))
        {
            relations.Add(new RelationDraft(qualifiedName, name, RelationKind.Exports));
        }
    }

    // ------------------------------------------------------------------
    // Calls
    // ------------------------------------------------------------------

    private void AddCallRelations(
        TS.Node root, List<SymbolDraft> symbolDrafts, List<RelationDraft> relations, IReadOnlySet<string>? projectSymbolNames)
    {
        // A callee name matching either this file's own declared functions/methods, or any symbol
        // already indexed elsewhere in the project (projectSymbolNames), counts as a relation --
        // otherwise a call to an external/library function (`console.log(...)`, an imported helper,
        // etc.) with no corresponding project symbol is correctly excluded.
        var knownNames = new HashSet<string>(StringComparer.Ordinal);
        if (projectSymbolNames is not null)
        {
            knownNames.UnionWith(projectSymbolNames);
        }

        foreach (var draft in symbolDrafts)
        {
            if (draft.Kind is SymbolKind.Function or SymbolKind.Method)
            {
                knownNames.Add(draft.Name);
            }
        }

        if (knownNames.Count == 0)
        {
            return;
        }

        var seen = new HashSet<(string Source, string Callee)>();

        using (var query = new TS.Query(_language, CallsQuery))
        {
            foreach (var match in query.Execute(root).Matches)
            {
                var calleeNode = match.Captures.FirstOrDefault(c => c.Name == "call.callee")?.Node;
                if (calleeNode is not null)
                {
                    TryRecordCall(calleeNode, knownNames, seen, relations);
                }
            }
        }

        // Method-group/delegate references (see MethodGroupReferencesQuery's remarks): recorded as
        // Calls too, through the same seen-dedup, rather than as a new RelationKind — a reference
        // that hands a function to something else conceptually IS a call site.
        using (var referencesQuery = new TS.Query(_language, MethodGroupReferencesQuery))
        {
            foreach (var match in referencesQuery.Execute(root).Matches)
            {
                var refNode = match.Captures.FirstOrDefault(c => c.Name == "ref.name")?.Node;
                if (refNode is not null)
                {
                    TryRecordCall(refNode, knownNames, seen, relations);
                }
            }
        }
    }

    private static void TryRecordCall(
        TS.Node calleeNode,
        HashSet<string> knownNames,
        HashSet<(string Source, string Callee)> seen,
        List<RelationDraft> relations)
    {
        var callee = calleeNode.Text;
        if (!knownNames.Contains(callee))
        {
            return;
        }

        var enclosing = FindEnclosingCallable(calleeNode);
        if (enclosing is null)
        {
            return; // a call outside any function/method body (e.g. a top-level statement) - out of scope
        }

        var (sourceQualifiedName, sourceName) = QualifiedNameOfCallable(enclosing);
        if (sourceName is null || !seen.Add((sourceQualifiedName, callee)))
        {
            return;
        }

        relations.Add(new RelationDraft(sourceQualifiedName, callee, RelationKind.Calls));
    }

    private static TS.Node? FindEnclosingCallable(TS.Node node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (CallEnclosingTypes.Contains(current.Type))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// A <c>method_definition</c>/<c>function_declaration</c> names itself directly; an
    /// <c>arrow_function</c>/<c>function_expression</c> only has a name when it's the <c>value</c> of
    /// a <c>variable_declarator</c> (the "varfn" construct) - anything else (an inline callback
    /// argument, an IIFE, a function returned from another function) has no name to attribute the
    /// call to and is skipped, mirroring TreeSitterCSharpParser's equivalent "no enclosing named
    /// declaration" skip.
    /// </summary>
    private static (string QualifiedName, string? Name) QualifiedNameOfCallable(TS.Node callable)
    {
        if (NamedFunctionContainerTypes.Contains(callable.Type))
        {
            var name = TreeSitterQueryHelpers.TryGetField(callable, "name")?.Text;
            return name is null ? (string.Empty, null) : ComputeQualifiedNameTuple(callable, name);
        }

        // arrow_function or function_expression: only named when assigned to a variable_declarator.
        if (callable.Parent is { Type: "variable_declarator" } declarator)
        {
            var name = TreeSitterQueryHelpers.TryGetField(declarator, "name")?.Text;
            return name is null ? (string.Empty, null) : ComputeQualifiedNameTuple(declarator, name);
        }

        return (string.Empty, null);
    }

    private static (string, string?) ComputeQualifiedNameTuple(TS.Node declNode, string name)
    {
        var (qualifiedName, _) = ComputeQualifiedName(declNode, name);
        return (qualifiedName, name);
    }

    // ------------------------------------------------------------------
    // Qualified-name / nesting context
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a symbol's fully-qualified dotted name by walking up <see cref="TS.Node.Parent"/> from
    /// <paramref name="declNode"/> collecting every enclosing named container (class/interface, or a
    /// named function-like construct: <c>function_declaration</c>, <c>method_definition</c>, or a
    /// <c>variable_declarator</c> whose value is an <c>arrow_function</c>/<c>function_expression</c>),
    /// plus the qualified name of the immediate enclosing container for
    /// <see cref="SymbolDraft.ParentQualifiedName"/> (null when there is no enclosing container at
    /// all - a genuinely top-level declaration).
    /// </summary>
    private static (string QualifiedName, string? ParentQualifiedName) ComputeQualifiedName(TS.Node declNode, string name)
    {
        var containers = CollectContext(declNode);
        var prefix = containers.Count == 0 ? null : string.Join('.', containers);
        var qualifiedName = TreeSitterQueryHelpers.Combine(prefix, name);
        return (qualifiedName, prefix);
    }

    private static List<string> CollectContext(TS.Node node)
    {
        var containers = new List<string>();
        var current = node.Parent;

        while (current is not null)
        {
            if (TypeContainerTypes.Contains(current.Type) || NamedFunctionContainerTypes.Contains(current.Type))
            {
                var containerName = TreeSitterQueryHelpers.TryGetField(current, "name")?.Text;
                if (containerName is not null)
                {
                    containers.Add(containerName);
                }
            }
            else if (current.Type == "variable_declarator" &&
                     TreeSitterQueryHelpers.TryGetField(current, "value") is { Type: "arrow_function" or "function_expression" })
            {
                var containerName = TreeSitterQueryHelpers.TryGetField(current, "name")?.Text;
                if (containerName is not null)
                {
                    containers.Add(containerName);
                }
            }

            current = current.Parent;
        }

        containers.Reverse();
        return containers;
    }

    private static TreeSitterParsedTree RequireTree(ParsedAst ast) =>
        ast.Tree as TreeSitterParsedTree
        ?? throw new ArgumentException(
            "ParsedAst.Tree was not produced by a TreeSitter*Parser.", nameof(ast));

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
