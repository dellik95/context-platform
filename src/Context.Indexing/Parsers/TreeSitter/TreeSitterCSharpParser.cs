using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Real tree-sitter-backed <see cref="ILanguageParser"/> for C# (grammar id <c>"c-sharp"</c>),
/// registered alongside (not replacing) <c>RegexHeuristicParser</c> — see
/// <c>ServiceCollectionExtensions.AddContextIndexing</c> for which one is actually wired up by
/// default. Extracts classes/interfaces/structs/enums/methods/constructors/properties/fields and a
/// best-effort Calls/Extends/Implements/Imports relation set, all unresolved-by-name per
/// <see cref="RelationDraft"/>'s contract.
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
/// <b><c>previousTree</c>:</b> intentionally ignored, exactly like <c>RegexHeuristicParser</c> —
/// every real caller in this codebase discards a <see cref="ParsedAst"/> immediately after its one
/// Extract call (see <see cref="TreeSitterParsedTree"/>'s remarks), so a caller-supplied
/// <paramref name="previousTree"/> would, under this disposal design, already have had its native
/// <see cref="TS.Tree"/> disposed by the time it could be reused — accepting it anyway would be an
/// attractive nuisance. Always re-parses from scratch.
/// </para>
/// </remarks>
public sealed class TreeSitterCSharpParser : ILanguageParser, IDisposable
{
    private const string DocLinePrefix = "///";

    private static readonly HashSet<string> TypeDeclarationTypes =
        new(StringComparer.Ordinal) { "class_declaration", "interface_declaration", "struct_declaration", "enum_declaration" };

    // One pattern per symbol kind; every pattern captures the whole declaration as "<prefix>.def"
    // and its name identifier as "<prefix>.name" so a single query execution + one dictionary
    // lookup per match handles every kind uniformly (see ExtractSymbols).
    private const string SymbolsQuery = """
        (class_declaration name: (identifier) @class.name) @class.def
        (interface_declaration name: (identifier) @interface.name) @interface.def
        (struct_declaration name: (identifier) @struct.name) @struct.def
        (enum_declaration name: (identifier) @enum.name) @enum.def
        (method_declaration name: (identifier) @method.name) @method.def
        (constructor_declaration name: (identifier) @method.name) @method.def
        (property_declaration name: (identifier) @property.name) @property.def
        (field_declaration (variable_declaration (variable_declarator name: (identifier) @field.name))) @field.def
        """;

    // base_list is deliberately queried only off class/interface/struct declarations (never enum:
    // an enum's base_list is its underlying integral type, e.g. "enum Color : byte", not
    // inheritance — see ExtractRelations remarks).
    private const string BaseListQuery = """
        (class_declaration name: (identifier) @class.name (base_list) @class.bases) @class.def
        (interface_declaration name: (identifier) @interface.name (base_list) @interface.bases) @interface.def
        (struct_declaration name: (identifier) @struct.name (base_list) @struct.bases) @struct.def
        """;

    private const string ImportsQuery = """
        (using_directive (identifier) @import.name)
        (using_directive (qualified_name) @import.name)
        """;

    // Two shapes for a real invocation: a bare call ("Foo()", same class or static-import-like
    // context) and a member-access call ("this.Foo()"/"x.Foo()"/"Type.Foo()"/every LINQ call like
    // ".Select(...)") -- the latter is the overwhelming majority of real-world C# calls, and its
    // callee sits at the member_access_expression's "name" field (verified against a real parse
    // tree: "x.Foo()" is an invocation_expression whose function is a member_access_expression with
    // expression:"x" name:"Foo"; "this.Bar()" is the same shape minus the expression field).
    private const string InvocationsQuery = """
        (invocation_expression function: (identifier) @call.callee) @call.expr
        (invocation_expression function: (member_access_expression name: (identifier) @call.callee)) @call.expr
        """;

    // A method named but never actually invoked -- no invocation_expression wraps it at all, so no
    // query targeting invocations (above) can ever catch it; it needs this separate query. Two
    // shapes covered, both verified against a real parse tree: a method-group passed as a bare
    // argument (".Select(SomeMethod)" -- the "argument" node's direct child is just the identifier,
    // not an invocation_expression, which is what makes this distinguishable from ".Select(Foo())")
    // and an event subscription/unsubscription ("obj.Event += Handler;"/"-="). Deliberately NOT
    // covered: a qualified reference used the same way (".Select(obj.Method)") -- its callee sits
    // behind a member_access_expression, not a bare identifier, and there's no symbol-resolution
    // pass here to know which "obj" it is.
    private const string MethodGroupReferencesQuery = """
        (argument (identifier) @ref.name)
        (assignment_expression left: (_) "+=" right: (identifier) @ref.name)
        (assignment_expression left: (_) "-=" right: (identifier) @ref.name)
        """;

    private const string MethodNamesQuery = """
        (method_declaration name: (identifier) @name)
        (constructor_declaration name: (identifier) @name)
        """;

    private readonly TS.Language _language = new("c-sharp");
    private bool _disposed;

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

    public IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast)
    {
        var wrapper = RequireTree(ast);
        try
        {
            var root = wrapper.Tree.RootNode;
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
                    "struct" => SymbolKind.Struct,
                    "enum" => SymbolKind.Enum,
                    "method" => SymbolKind.Method,
                    "property" => SymbolKind.Property,
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

                // A member kind must be nested in a type; if it isn't (malformed/unexpected
                // grammar shape), there is no sensible ParentSymbolId to hang it off — skip it.
                if (kind is not (SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum)
                    && parentQualifiedName is null)
                {
                    continue;
                }

                var bodyFieldName = kind == SymbolKind.Property ? "accessors" : "body";
                var bodyNode = TreeSitterQueryHelpers.TryGetField(defNode, bodyFieldName);

                string signatureText;
                string bodyText;
                if (bodyNode is not null && defNode.Text.EndsWith(bodyNode.Text, StringComparison.Ordinal))
                {
                    signatureText = defNode.Text[..^bodyNode.Text.Length].Trim();
                    bodyText = defNode.Text;
                }
                else
                {
                    // Headerless case (interface member with no body, a field declaration, or a
                    // property whose accessors couldn't be matched textually): mirrors
                    // RegexHeuristicParser's convention for a symbol with no real body — the
                    // whole declaration is the signature and BodyText is empty.
                    signatureText = defNode.Text.Trim();
                    bodyText = string.Empty;
                }

                var docComment = TreeSitterQueryHelpers.ExtractLeadingDocComment(defNode, DocLinePrefix);

                drafts.Add(new SymbolDraft(
                    name, qualifiedName, kind.Value,
                    defNode.StartPosition.Row + 1, defNode.EndPosition.Row + 1,
                    signatureText, bodyText, docComment, parentQualifiedName));
            }

            return drafts;
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

            AddInheritanceRelations(root, relations);
            AddImportRelations(root, relations);
            AddCallRelations(root, relations, projectSymbolNames);

            return relations;
        }
        finally
        {
            wrapper.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Extends / Implements
    // ------------------------------------------------------------------

    private void AddInheritanceRelations(TS.Node root, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, BaseListQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            var captures = match.Captures.ToDictionary(c => c.Name, c => c.Node);
            var defEntry = captures.FirstOrDefault(kv => kv.Key.EndsWith(".def", StringComparison.Ordinal));
            var basesEntry = captures.FirstOrDefault(kv => kv.Key.EndsWith(".bases", StringComparison.Ordinal));
            var nameEntry = captures.FirstOrDefault(kv => kv.Key.EndsWith(".name", StringComparison.Ordinal));
            if (defEntry.Key is null || basesEntry.Key is null || nameEntry.Key is null)
            {
                continue;
            }

            var defNode = defEntry.Value;
            var (qualifiedName, _) = ComputeQualifiedName(defNode, nameEntry.Value.Text);

            // struct/interface base-list entries can only be interfaces; a class's entries are
            // disambiguated per-name by the "IFoo" naming convention, exactly like
            // RegexHeuristicParser.ParseCSharpInheritance (no symbol-resolution pass exists here
            // to know for certain which one is the base class).
            var allImplements = defNode.Type is "struct_declaration" or "interface_declaration";

            foreach (var baseEntry in basesEntry.Value.NamedChildren)
            {
                var baseName = BaseTypeName(baseEntry);
                if (string.IsNullOrEmpty(baseName))
                {
                    continue;
                }

                var kind = allImplements || LooksLikeInterfaceName(baseName) ? RelationKind.Implements : RelationKind.Extends;
                relations.Add(new RelationDraft(qualifiedName, baseName, kind));
            }
        }
    }

    private static string BaseTypeName(TS.Node baseListEntry) =>
        baseListEntry.Type == "generic_name"
            ? baseListEntry.FirstNamedChild?.Text ?? baseListEntry.Text
            : baseListEntry.Text;

    private static bool LooksLikeInterfaceName(string name) =>
        name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);

    // ------------------------------------------------------------------
    // Imports
    // ------------------------------------------------------------------

    // RelationDraft has no file-level "source" concept (SourceQualifiedName is always a symbol's
    // qualified name), so a using_directive's source is the nearest enclosing namespace name (or
    // "" for one at the very top of the file). That name never matches a persisted Symbol row, so
    // UpdateGraphJobHandler's symbol-only source lookup currently drops these relations rather than
    // turning them into graph edges — a real gap, but one this parser can't close alone (it would
    // need a File-sourced relation concept upstream); left as a documented limitation rather than
    // invented with a made-up symbol source that would be actively misleading.
    private void AddImportRelations(TS.Node root, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, ImportsQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            foreach (var capture in match.Captures)
            {
                var enclosingNamespace = GetEnclosingNamespace(capture.Node);
                relations.Add(new RelationDraft(enclosingNamespace, capture.Node.Text, RelationKind.Imports));
            }
        }
    }

    private static string GetEnclosingNamespace(TS.Node node)
    {
        var (namespaces, _) = CollectContext(node);
        return string.Join('.', namespaces);
    }

    // ------------------------------------------------------------------
    // Calls
    // ------------------------------------------------------------------

    private void AddCallRelations(TS.Node root, List<RelationDraft> relations, IReadOnlySet<string>? projectSymbolNames)
    {
        var knownMethodNames = new HashSet<string>(StringComparer.Ordinal);
        if (projectSymbolNames is not null)
        {
            knownMethodNames.UnionWith(projectSymbolNames);
        }

        using (var namesQuery = new TS.Query(_language, MethodNamesQuery))
        {
            foreach (var match in namesQuery.Execute(root).Matches)
            {
                foreach (var capture in match.Captures)
                {
                    knownMethodNames.Add(capture.Node.Text);
                }
            }
        }

        if (knownMethodNames.Count == 0)
        {
            return;
        }

        var seen = new HashSet<(string Source, string Callee)>();

        using (var invocationsQuery = new TS.Query(_language, InvocationsQuery))
        {
            foreach (var match in invocationsQuery.Execute(root).Matches)
            {
                var calleeNode = match.Captures.FirstOrDefault(c => c.Name == "call.callee")?.Node;
                if (calleeNode is not null)
                {
                    TryRecordCall(calleeNode, knownMethodNames, seen, relations);
                }
            }
        }

        // Method-group/delegate references (see MethodGroupReferencesQuery's remarks): recorded as
        // Calls too, through the same seen-dedup, rather than as a new RelationKind — a reference
        // that hands a method to a delegate/event conceptually IS a call site.
        using (var referencesQuery = new TS.Query(_language, MethodGroupReferencesQuery))
        {
            foreach (var match in referencesQuery.Execute(root).Matches)
            {
                var refNode = match.Captures.FirstOrDefault(c => c.Name == "ref.name")?.Node;
                if (refNode is not null)
                {
                    TryRecordCall(refNode, knownMethodNames, seen, relations);
                }
            }
        }
    }

    private static void TryRecordCall(
        TS.Node calleeNode,
        HashSet<string> knownMethodNames,
        HashSet<(string Source, string Callee)> seen,
        List<RelationDraft> relations)
    {
        var callee = calleeNode.Text;
        if (!knownMethodNames.Contains(callee))
        {
            return;
        }

        var enclosingMethod = FindEnclosingMethodOrConstructor(calleeNode);
        if (enclosingMethod is null)
        {
            return; // a call outside any method/constructor body (e.g. a field initializer) — out of scope, matching the task's "a method body containing an invocation" wording
        }

        var methodName = TreeSitterQueryHelpers.TryGetField(enclosingMethod, "name")?.Text;
        if (methodName is null)
        {
            return;
        }

        var (sourceQualifiedName, _) = ComputeQualifiedName(enclosingMethod, methodName);
        if (!seen.Add((sourceQualifiedName, callee)))
        {
            return;
        }

        relations.Add(new RelationDraft(sourceQualifiedName, callee, RelationKind.Calls));
    }

    private static TS.Node? FindEnclosingMethodOrConstructor(TS.Node node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current.Type is "method_declaration" or "constructor_declaration")
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Qualified-name / nesting context
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a symbol's fully-qualified dotted name (enclosing namespace(s) + enclosing type(s) +
    /// its own name) by walking up <see cref="TS.Node.Parent"/> from <paramref name="declNode"/>,
    /// plus the qualified name of its immediate enclosing type, if any (for
    /// <see cref="SymbolDraft.ParentQualifiedName"/> — null when the only enclosing containers are
    /// namespaces, since namespaces are never themselves emitted as symbols here).
    /// </summary>
    private static (string QualifiedName, string? ParentQualifiedName) ComputeQualifiedName(TS.Node declNode, string name)
    {
        var (namespaces, types) = CollectContext(declNode);
        var fullPrefix = JoinNonEmpty(namespaces, types);
        var parentQualifiedName = types.Count > 0 ? fullPrefix : null;
        var qualifiedName = TreeSitterQueryHelpers.Combine(fullPrefix, name);
        return (qualifiedName, parentQualifiedName);
    }

    private static string? JoinNonEmpty(List<string> namespaces, List<string> types)
    {
        if (namespaces.Count == 0 && types.Count == 0)
        {
            return null;
        }

        return string.Join('.', namespaces.Concat(types));
    }

    /// <summary>
    /// Walks up from <paramref name="node"/> collecting enclosing namespace name(s) (outer-to-inner)
    /// and enclosing type name(s) (outer-to-inner). Handles a block-scoped
    /// <c>namespace X { ... }</c> ancestor (found directly by walking <c>Parent</c>) and a
    /// file-scoped <c>namespace X;</c> "ancestor" (which is structurally a preceding sibling of
    /// <paramref name="node"/>'s topmost ancestor under <c>compilation_unit</c>, not a real
    /// ancestor, since it has no body to nest anything under — C# allows at most one per file).
    /// </summary>
    private static (List<string> Namespaces, List<string> Types) CollectContext(TS.Node node)
    {
        var namespaces = new List<string>();
        var types = new List<string>();
        var current = node.Parent;

        while (current is not null)
        {
            if (TypeDeclarationTypes.Contains(current.Type))
            {
                var typeName = TreeSitterQueryHelpers.TryGetField(current, "name")?.Text;
                if (typeName is not null)
                {
                    types.Add(typeName);
                }
            }
            else if (current.Type == "namespace_declaration")
            {
                var nsName = TreeSitterQueryHelpers.TryGetField(current, "name")?.Text;
                if (nsName is not null)
                {
                    namespaces.Add(nsName);
                }
            }
            else if (current.Type == "compilation_unit")
            {
                var fileScoped = current.NamedChildren.FirstOrDefault(c =>
                    c.Type == "file_scoped_namespace_declaration" && c.StartPosition.Row < node.StartPosition.Row);
                if (fileScoped is not null)
                {
                    var nsName = TreeSitterQueryHelpers.TryGetField(fileScoped, "name")?.Text;
                    if (nsName is not null)
                    {
                        namespaces.Add(nsName);
                    }
                }
            }

            current = current.Parent;
        }

        types.Reverse();
        namespaces.Reverse();
        return (namespaces, types);
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
