using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

public sealed partial class TreeSitterCSharpParser
{
    // One pattern per symbol kind; every pattern captures the whole declaration as "<prefix>.def"
    // and its name identifier as "<prefix>.name" so a single query execution + one dictionary
    // lookup per match handles every kind uniformly.
    private const string SymbolsQuery = """
        (class_declaration name: (identifier) @class.name) @class.def
        (interface_declaration name: (identifier) @interface.name) @interface.def
        (struct_declaration name: (identifier) @struct.name) @struct.def
        (enum_declaration name: (identifier) @enum.name) @enum.def
        (record_declaration name: (identifier) @record.name) @record.def
        (method_declaration name: (identifier) @method.name) @method.def
        (constructor_declaration name: (identifier) @method.name) @method.def
        (property_declaration name: (identifier) @property.name) @property.def
        (field_declaration (variable_declaration (variable_declarator name: (identifier) @field.name))) @field.def
        """;

    // Optional separate node type used by some tree-sitter-c-sharp grammar versions for `record struct`.
    private const string RecordStructSymbolsQuery = """
        (record_struct_declaration name: (identifier) @record_struct.name) @record_struct.def
        """;

    public IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast)
    {
        var wrapper = RequireTree(ast);
        try
        {
            var root = wrapper.Tree.RootNode;
            var drafts = new List<SymbolDraft>();

            CollectSymbolDrafts(root, SymbolsQuery, drafts);
            if (_supportsRecordStructNode)
            {
                CollectSymbolDrafts(root, RecordStructSymbolsQuery, drafts);
            }

            return drafts;
        }
        finally
        {
            wrapper.Dispose();
        }
    }

    private void CollectSymbolDrafts(TS.Node root, string querySource, List<SymbolDraft> drafts)
    {
        using var query = new TS.Query(_language, querySource);
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
            var kind = prefix switch
            {
                "class" => SymbolKind.Class,
                "interface" => SymbolKind.Interface,
                "struct" or "record_struct" => SymbolKind.Struct,
                "record" => IsRecordStructNode(defNode) ? SymbolKind.Struct : SymbolKind.Class,
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

            var name = nameNode.Text;
            var (qualifiedName, parentQualifiedName) = ComputeQualifiedName(defNode, name);

            // A member kind must be nested in a type; if it isn't, skip it.
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
                // Headerless case (interface member, field, positional record, or unmatched property accessors).
                signatureText = defNode.Text.Trim();
                bodyText = string.Empty;
            }

            var docComment = TreeSitterQueryHelpers.ExtractLeadingDocComment(defNode, DocLinePrefix);

            drafts.Add(new SymbolDraft(
                name, qualifiedName, kind.Value,
                defNode.StartPosition.Row + 1, defNode.EndPosition.Row + 1,
                signatureText, bodyText, docComment, parentQualifiedName));
        }
    }

    private static bool IsRecordStructNode(TS.Node defNode) =>
        defNode.Type == "record_struct_declaration"
        || defNode.Children.Any(c => c.Type == "struct" || c.Text == "struct");

    /// <summary>
    /// Builds a symbol's fully-qualified dotted name (enclosing namespace(s) + enclosing type(s) +
    /// its own name) by walking up <see cref="TS.Node.Parent"/> from <paramref name="declNode"/>.
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
    /// Walks up from <paramref name="node"/> collecting enclosing namespace name(s) and enclosing
    /// type name(s), handling both block-scoped and file-scoped namespaces.
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
}
