using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Real tree-sitter-backed <see cref="ILanguageParser"/> for Python (grammar id <c>"python"</c>),
/// registered alongside (not replacing) <c>RegexHeuristicParser</c> — see
/// <c>ServiceCollectionExtensions.AddContextIndexing</c> for which one is actually wired up by
/// default. Extracts classes and functions/methods (a <c>function_definition</c> nested directly
/// in a <c>class_definition</c>'s body gets <see cref="SymbolKind.Method"/>, one at module level or
/// nested in another function gets <see cref="SymbolKind.Function"/>) plus a best-effort
/// Imports/Extends/Calls relation set, all unresolved-by-name per <see cref="RelationDraft"/>'s
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime:</b> identical scheme to <see cref="TreeSitterCSharpParser"/> — the
/// <see cref="TS.Language"/> is created once in the constructor and kept for this instance's
/// entire lifetime (parsers are registered as DI singletons); each <see cref="Parse"/> call
/// creates a fresh <see cref="TS.Parser"/>/<see cref="TS.Tree"/> pair boxed into a
/// <see cref="TreeSitterParsedTree"/>, disposed as the last step of whichever of
/// <see cref="ExtractSymbols"/>/<see cref="ExtractRelations"/> runs on it (never both — see
/// <see cref="TreeSitterParsedTree"/>'s remarks).
/// </para>
/// <para>
/// <b>Doc comments are docstrings, not comments:</b> Python has no leading-comment doc-comment
/// convention like C#'s <c>///</c>; PEP 257 instead makes a docstring the class/function body's
/// own first statement, when that statement is a bare string literal. <see cref="ExtractDocstring"/>
/// implements that directly by looking at the body block's first named child, rather than reusing
/// <see cref="TreeSitterQueryHelpers.ExtractLeadingDocComment"/> (which looks at a *preceding
/// sibling* comment node) — a genuinely different mechanism. A plain <c>#</c> comment immediately
/// above a <c>def</c>/<c>class</c> is deliberately not treated as a doc comment.
/// </para>
/// <para>
/// <b>Decorators:</b> a <c>@decorator</c>-wrapped class/function is still matched by
/// <see cref="SymbolsQuery"/> — the query targets <c>class_definition</c>/<c>function_definition</c>
/// directly, and a tree-sitter query matches a pattern anywhere in the tree regardless of its
/// ancestors, so the wrapping <c>decorated_definition</c> node (which Python's grammar always
/// collapses stacked decorators into exactly one of, however many <c>@x</c> lines precede the
/// definition) is transparent to the match itself. It is, however, deliberately picked up as the
/// symbol's <see cref="OuterNode"/> so decorator lines count as part of the declaration's line
/// range/signature/body text, while being skipped over transparently (never itself matched) when
/// walking upward for qualified-name and method-vs-function context — see
/// <see cref="SyntacticParent"/>/<see cref="CollectContainers"/>.
/// </para>
/// </remarks>
public sealed class TreeSitterPythonParser : ILanguageParser, IDisposable
{
    private const string DecoratedDefinitionType = "decorated_definition";
    private const string ClassDefinitionType = "class_definition";
    private const string FunctionDefinitionType = "function_definition";
    private const string BlockType = "block";
    private const string StringType = "string";
    private const string StringContentType = "string_content";
    private const string KeywordArgumentType = "keyword_argument";

    // One pattern per symbol kind, each capturing the whole declaration as "<prefix>.def" and its
    // name identifier as "<prefix>.name" — mirrors TreeSitterCSharpParser.SymbolsQuery's convention
    // so ExtractSymbols can handle every kind with one query execution + one dictionary lookup per
    // match. Method vs. Function is *not* decided by the query (Python has one node type for both)
    // but by IsMethod's structural check below.
    private const string SymbolsQuery = """
        (class_definition name: (identifier) @class.name) @class.def
        (function_definition name: (identifier) @function.name) @function.def
        """;

    // superclasses is deliberately queried only off class_definition — the only Python construct
    // with a base list. Every entry becomes Extends in AddInheritanceRelations: Python has no
    // "implements" keyword/concept to disambiguate against, unlike C#'s base_list.
    private const string BaseClassesQuery = """
        (class_definition name: (identifier) @class.name superclasses: (argument_list) @class.bases) @class.def
        """;

    // Covers "import x" / "import x as y" / "import x, y" (a repeated `name` field produces one
    // match per occurrence) and both "from x import ..." and "from . import ..." /
    // "from ..pkg import ..." (module_name is a dotted_name for the former, a relative_import for
    // the latter two — `(_)` matches either uniformly, capturing the module's full text either way).
    private const string ImportsQuery = """
        (import_statement name: (dotted_name) @import.name)
        (import_statement name: (aliased_import name: (dotted_name) @import.name))
        (import_from_statement module_name: (_) @import.name)
        """;

    private const string FunctionNamesQuery = "(function_definition name: (identifier) @name)";

    // Two shapes for a real invocation, mirroring TreeSitterCSharpParser.InvocationsQuery: a plain
    // `foo()` call, and a member-access call (`self.foo()`/`obj.foo()`) — verified against a real
    // parse tree: `self.foo()` is a `call` node whose function is an `attribute` node with
    // object:"self" attribute:"foo" (the callee name lives on the attribute field). A class
    // instantiation (`Animal(name)`) is still naturally excluded downstream either way (never added
    // to knownFunctionNames, since that set is built only from function_definition names, not class
    // names) without any extra filtering being needed.
    private const string CallsQuery = """
        (call function: (identifier) @call.callee) @call.expr
        (call function: (attribute attribute: (identifier) @call.callee)) @call.expr
        """;

    // A function/method named but never actually invoked — passed as a bare-name call argument
    // (`map(some_func, items)`, `items.select(some_func)`). Verified against a real parse tree:
    // Python's `argument_list` wraps each positional argument directly with no per-argument wrapper
    // node (same shape as JS/TS's `arguments`, unlike C#'s `argument`-wrapped one). Anchored to a
    // `call`'s own `arguments` field — not a bare `(argument_list (identifier))` — because
    // `argument_list` is also the node type used for a class's base-class list
    // (`BaseClassesQuery`'s `superclasses` field); anchoring keeps this pass from ever matching a
    // base-class name. Deliberately NOT covered: an event/callback-style attribute assignment
    // (`widget.on_click = handler`) — a plain `=` here is indistinguishable from an ordinary data
    // assignment without symbol resolution, unlike C#'s dedicated `+=`/`-=` event-subscription
    // operator.
    private const string MethodGroupReferencesQuery =
        "(call arguments: (argument_list (identifier) @ref.name))";

    private readonly TS.Language _language = new("python");
    private bool _disposed;

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py" };

    public ParsedAst Parse(string sourceText, ParsedAst? previousTree = null)
    {
        // previousTree is intentionally unused — see TreeSitterCSharpParser's remarks (identical
        // rationale: every real caller discards a ParsedAst after its one Extract call, so a
        // caller-supplied previousTree would already have had its native Tree disposed).
        var parser = new TS.Parser(_language);
        var tree = parser.Parse(sourceText);
        if (tree is null)
        {
            parser.Dispose();
            throw new InvalidOperationException("tree-sitter produced a null Tree for Python source.");
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

                var defNode = defEntry.Value;
                var name = nameNode.Text;

                SymbolKind kind;
                if (prefix == "class")
                {
                    kind = SymbolKind.Class;
                }
                else if (prefix == "function")
                {
                    kind = IsMethod(defNode) ? SymbolKind.Method : SymbolKind.Function;
                }
                else
                {
                    continue;
                }

                var (qualifiedName, parentQualifiedName) = ComputeQualifiedName(defNode, name);

                // A method must be nested in a class; if it somehow isn't (malformed/unexpected
                // grammar shape), there is no sensible ParentSymbolId to hang it off — skip it,
                // mirroring TreeSitterCSharpParser's equivalent defensive check for its member kinds.
                if (kind == SymbolKind.Method && parentQualifiedName is null)
                {
                    continue;
                }

                var outerNode = OuterNode(defNode);
                var bodyNode = TreeSitterQueryHelpers.TryGetField(defNode, "body");

                string signatureText;
                string bodyText;
                if (bodyNode is not null && outerNode.Text.EndsWith(bodyNode.Text, StringComparison.Ordinal))
                {
                    signatureText = outerNode.Text[..^bodyNode.Text.Length].TrimEnd();
                    bodyText = outerNode.Text;
                }
                else
                {
                    // Headerless/unexpected case: whole declaration is the signature, no body text
                    // — mirrors RegexHeuristicParser's and TreeSitterCSharpParser's convention.
                    signatureText = outerNode.Text.Trim();
                    bodyText = string.Empty;
                }

                var docComment = bodyNode is null ? null : ExtractDocstring(bodyNode);

                drafts.Add(new SymbolDraft(
                    name, qualifiedName, kind,
                    outerNode.StartPosition.Row + 1, outerNode.EndPosition.Row + 1,
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
    // Extends
    // ------------------------------------------------------------------

    private void AddInheritanceRelations(TS.Node root, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, BaseClassesQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            var captures = match.Captures.ToDictionary(c => c.Name, c => c.Node);
            if (!captures.TryGetValue("class.def", out var defNode)
                || !captures.TryGetValue("class.name", out var nameNode)
                || !captures.TryGetValue("class.bases", out var basesNode))
            {
                continue;
            }

            var (qualifiedName, _) = ComputeQualifiedName(defNode, nameNode.Text);

            foreach (var baseEntry in basesNode.NamedChildren)
            {
                // `class Foo(Base, metaclass=ABCMeta):` — metaclass=... (and any other keyword
                // argument in the base list) is a class-construction keyword argument, not a base
                // class; skip it.
                if (baseEntry.Type == KeywordArgumentType)
                {
                    continue;
                }

                var baseName = baseEntry.Text;
                if (string.IsNullOrEmpty(baseName))
                {
                    continue;
                }

                relations.Add(new RelationDraft(qualifiedName, baseName, RelationKind.Extends));
            }
        }
    }

    // ------------------------------------------------------------------
    // Imports
    // ------------------------------------------------------------------

    // Mirrors TreeSitterCSharpParser.AddImportRelations' rationale verbatim: RelationDraft has no
    // file-level "source" concept, so an import's source is its nearest enclosing class/function
    // qualified name (or "" for one at module level — the overwhelmingly common case). That empty
    // source never matches a persisted Symbol row, so UpdateGraphJobHandler's symbol-only source
    // lookup currently drops those relations rather than turning them into graph edges — a
    // documented limitation this parser can't close alone (it would need a File-sourced relation
    // concept upstream).
    private void AddImportRelations(TS.Node root, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, ImportsQuery);
        foreach (var match in query.Execute(root).Matches)
        {
            foreach (var capture in match.Captures)
            {
                var enclosing = string.Join('.', CollectContainers(capture.Node));
                relations.Add(new RelationDraft(enclosing, capture.Node.Text, RelationKind.Imports));
            }
        }
    }

    // ------------------------------------------------------------------
    // Calls
    // ------------------------------------------------------------------

    private void AddCallRelations(TS.Node root, List<RelationDraft> relations, IReadOnlySet<string>? projectSymbolNames)
    {
        var knownFunctionNames = new HashSet<string>(StringComparer.Ordinal);
        if (projectSymbolNames is not null)
        {
            knownFunctionNames.UnionWith(projectSymbolNames);
        }

        using (var namesQuery = new TS.Query(_language, FunctionNamesQuery))
        {
            foreach (var match in namesQuery.Execute(root).Matches)
            {
                foreach (var capture in match.Captures)
                {
                    knownFunctionNames.Add(capture.Node.Text);
                }
            }
        }

        if (knownFunctionNames.Count == 0)
        {
            return;
        }

        var seen = new HashSet<(string Source, string Callee)>();

        using (var callsQuery = new TS.Query(_language, CallsQuery))
        {
            foreach (var match in callsQuery.Execute(root).Matches)
            {
                var calleeNode = match.Captures.FirstOrDefault(c => c.Name == "call.callee")?.Node;
                if (calleeNode is not null)
                {
                    TryRecordCall(calleeNode, knownFunctionNames, seen, relations);
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
                    TryRecordCall(refNode, knownFunctionNames, seen, relations);
                }
            }
        }
    }

    private static void TryRecordCall(
        TS.Node calleeNode,
        HashSet<string> knownFunctionNames,
        HashSet<(string Source, string Callee)> seen,
        List<RelationDraft> relations)
    {
        var callee = calleeNode.Text;
        if (!knownFunctionNames.Contains(callee))
        {
            return;
        }

        var enclosingFunction = FindEnclosingFunction(calleeNode);
        if (enclosingFunction is null)
        {
            return; // a call outside any function/method body (e.g. a decorator argument or module-level statement) — out of scope, matching the other parsers' "inside a body" wording
        }

        var functionName = TreeSitterQueryHelpers.TryGetField(enclosingFunction, "name")?.Text;
        if (functionName is null)
        {
            return;
        }

        var (sourceQualifiedName, _) = ComputeQualifiedName(enclosingFunction, functionName);
        if (!seen.Add((sourceQualifiedName, callee)))
        {
            return;
        }

        relations.Add(new RelationDraft(sourceQualifiedName, callee, RelationKind.Calls));
    }

    private static TS.Node? FindEnclosingFunction(TS.Node node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current.Type == FunctionDefinitionType)
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Docstrings
    // ------------------------------------------------------------------

    /// <summary>
    /// PEP 257 docstring: the first statement in a class/function body being a bare string
    /// literal. Unlike C#'s leading-<c>///</c>-comment convention this looks *inside* the body
    /// (its first named child) rather than at a preceding sibling — tree-sitter-python emits no
    /// wrapping "expression_statement" node around a bare string/assignment/call statement, so the
    /// body block's first named child is the <c>string</c> node itself when a docstring is present.
    /// </summary>
    /// <param name="bodyNode">The declaration's <c>body</c> field (a <c>block</c> node).</param>
    private static string? ExtractDocstring(TS.Node bodyNode)
    {
        var first = bodyNode.NamedChildren.FirstOrDefault();
        if (first is null || first.Type != StringType)
        {
            return null;
        }

        // Concatenate every string_content segment (ordinarily just one, even for a multi-line
        // triple-quoted docstring — the newline is part of that one segment's text already).
        var contentParts = TreeSitterQueryHelpers.DescendantsOfType(first, StringContentType).Select(n => n.Text).ToList();
        if (contentParts.Count == 0)
        {
            return null;
        }

        var text = string.Join(string.Empty, contentParts).Trim();
        return text.Length == 0 ? null : text;
    }

    // ------------------------------------------------------------------
    // Method-vs-function / qualified-name / nesting context
    // ------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="functionDef"/> is nested directly in a <c>class_definition</c>'s
    /// body (its own <c>block</c>, possibly through one wrapping <c>decorated_definition</c> for a
    /// <c>@decorator</c>-wrapped method) rather than at module level or inside another function.
    /// </summary>
    private static bool IsMethod(TS.Node functionDef)
    {
        var enclosingBlock = SyntacticParent(functionDef);
        return enclosingBlock is { Type: BlockType } && enclosingBlock.Parent is { Type: ClassDefinitionType };
    }

    /// <summary>
    /// <paramref name="node"/>'s parent, transparently skipping one wrapping
    /// <c>decorated_definition</c> — stacked decorators (<c>@a</c> <c>@b</c> <c>def f():</c>) still
    /// produce only a single <c>decorated_definition</c> around the one definition, so a single
    /// skip always suffices.
    /// </summary>
    private static TS.Node? SyntacticParent(TS.Node node) =>
        node.Parent is { Type: DecoratedDefinitionType } decorated ? decorated.Parent : node.Parent;

    /// <summary>
    /// The node whose text/line-range represents the symbol's full declaration: the wrapping
    /// <c>decorated_definition</c> when present (so decorator lines count as part of the
    /// declaration), otherwise <paramref name="defNode"/> itself.
    /// </summary>
    private static TS.Node OuterNode(TS.Node defNode) =>
        defNode.Parent is { Type: DecoratedDefinitionType } decorated ? decorated : defNode;

    /// <summary>
    /// Builds a symbol's fully-qualified dotted name (every enclosing class/function definition's
    /// name, outer-to-inner, plus its own) by walking up <see cref="TS.Node.Parent"/> from
    /// <paramref name="declNode"/>. A wrapping <c>decorated_definition</c> ancestor needs no
    /// special-casing here: it matches neither <c>class_definition</c> nor <c>function_definition</c>
    /// in <see cref="CollectContainers"/>'s walk, so it contributes nothing and the walk simply
    /// continues past it to the real enclosing container.
    /// </summary>
    private static (string QualifiedName, string? ParentQualifiedName) ComputeQualifiedName(TS.Node declNode, string name)
    {
        var containers = CollectContainers(declNode);
        string? prefix = containers.Count > 0 ? string.Join('.', containers) : null;
        return (TreeSitterQueryHelpers.Combine(prefix, name), prefix);
    }

    private static List<string> CollectContainers(TS.Node node)
    {
        var containers = new List<string>();
        var current = node.Parent;

        while (current is not null)
        {
            if (current.Type is ClassDefinitionType or FunctionDefinitionType)
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
            $"ParsedAst.Tree was not produced by {nameof(TreeSitterPythonParser)}.", nameof(ast));

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
