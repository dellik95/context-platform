using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

public sealed partial class TreeSitterCSharpParser
{
    // base_list is deliberately queried only off class/interface/struct/record declarations (never enum).
    private const string BaseListQuery = """
        (class_declaration name: (identifier) @class.name (base_list) @class.bases) @class.def
        (interface_declaration name: (identifier) @interface.name (base_list) @interface.bases) @interface.def
        (struct_declaration name: (identifier) @struct.name (base_list) @struct.bases) @struct.def
        (record_declaration name: (identifier) @record.name (base_list) @record.bases) @record.def
        """;

    private const string RecordStructBaseListQuery = """
        (record_struct_declaration name: (identifier) @record_struct.name (base_list) @record_struct.bases) @record_struct.def
        """;

    private const string ImportsQuery = """
        (using_directive (identifier) @import.name)
        (using_directive (qualified_name) @import.name)
        """;

    private const string InvocationsQuery = """
        (invocation_expression function: (identifier) @call.callee) @call.expr
        (invocation_expression function: (member_access_expression name: (identifier) @call.callee)) @call.expr
        """;

    private const string MethodGroupReferencesQuery = """
        (argument (identifier) @ref.name)
        (assignment_expression left: (_) "+=" right: (identifier) @ref.name)
        (assignment_expression left: (_) "-=" right: (identifier) @ref.name)
        """;

    private const string MethodNamesQuery = """
        (method_declaration name: (identifier) @name)
        (constructor_declaration name: (identifier) @name)
        """;

    public IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast, IReadOnlySet<string>? projectSymbolNames = null)
    {
        var wrapper = RequireTree(ast);
        try
        {
            var root = wrapper.Tree.RootNode;
            var relations = new List<RelationDraft>();

            AddInheritanceRelations(root, relations);
            AddImportRelations(root, relations);
            var recordedCalls = AddCallRelations(root, relations, projectSymbolNames);
            var recordedReferences = AddReferenceRelations(root, relations, projectSymbolNames);
            AddTestRelations(root, relations, recordedCalls, recordedReferences);

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
        CollectInheritanceRelations(root, BaseListQuery, relations);
        if (_supportsRecordStructNode)
        {
            CollectInheritanceRelations(root, RecordStructBaseListQuery, relations);
        }
    }

    private void CollectInheritanceRelations(TS.Node root, string querySource, List<RelationDraft> relations)
    {
        using var query = new TS.Query(_language, querySource);
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

            var allImplements = defNode.Type is "struct_declaration" or "interface_declaration" or "record_struct_declaration"
                || IsRecordStructNode(defNode);

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

    private static string BaseTypeName(TS.Node baseListEntry)
    {
        var typeNode = baseListEntry.Type == "primary_constructor_base_type"
            ? TreeSitterQueryHelpers.TryGetField(baseListEntry, "type") ?? baseListEntry.FirstNamedChild
            : baseListEntry;
        if (typeNode is null)
        {
            return string.Empty;
        }

        var raw = typeNode.Type == "generic_name"
            ? typeNode.FirstNamedChild?.Text ?? typeNode.Text
            : typeNode.Text;
        var dot = raw.LastIndexOf('.');
        return dot >= 0 ? raw[(dot + 1)..] : raw;
    }

    private static bool LooksLikeInterfaceName(string name) =>
        name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);

    // ------------------------------------------------------------------
    // Imports
    // ------------------------------------------------------------------

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

    private HashSet<(string Source, string Callee)> AddCallRelations(TS.Node root, List<RelationDraft> relations, IReadOnlySet<string>? projectSymbolNames)
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

        var seen = new HashSet<(string Source, string Callee)>();
        if (knownMethodNames.Count == 0)
        {
            return seen;
        }

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

        return seen;
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

        var enclosingCallable = FindEnclosingCallable(calleeNode);
        if (enclosingCallable is null)
        {
            return;
        }

        var callableName = TreeSitterQueryHelpers.TryGetField(enclosingCallable, "name")?.Text;
        if (callableName is null)
        {
            return;
        }

        var (sourceQualifiedName, _) = ComputeQualifiedName(enclosingCallable, callableName);
        if (!seen.Add((sourceQualifiedName, callee)))
        {
            return;
        }

        relations.Add(new RelationDraft(sourceQualifiedName, callee, RelationKind.Calls));
    }

    private static TS.Node? FindEnclosingCallable(TS.Node node)
    {
        var viaPropertyAccessorOrArrow = false;
        var current = node.Parent;
        while (current is not null)
        {
            if (current.Type is "method_declaration" or "constructor_declaration")
            {
                return current;
            }

            if (current.Type == "accessor_declaration")
            {
                viaPropertyAccessorOrArrow = true;
            }
            else if (current.Type == "arrow_expression_clause" && current.Parent?.Type == "property_declaration")
            {
                viaPropertyAccessorOrArrow = true;
            }
            else if (current.Type == "property_declaration")
            {
                return viaPropertyAccessorOrArrow ? current : null;
            }
            else if (current.Type == "field_declaration")
            {
                return null;
            }

            current = current.Parent;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // References & Tests
    // ------------------------------------------------------------------

    private static HashSet<(string Source, string Target)> AddReferenceRelations(
        TS.Node root, List<RelationDraft> relations, IReadOnlySet<string>? projectSymbolNames)
    {
        var knownTargets = CollectKnownTypeNames(root, projectSymbolNames);
        var seen = new HashSet<(string Source, string Target)>();
        if (knownTargets.Count == 0)
        {
            return seen;
        }

        WalkForReferences(root, currentSource: null, knownTargets, seen, relations);
        return seen;
    }

    private static HashSet<string> CollectKnownTypeNames(TS.Node root, IReadOnlySet<string>? projectSymbolNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (projectSymbolNames is not null)
        {
            names.UnionWith(projectSymbolNames);
        }

        CollectLocalTypeNames(root, names);
        return names;
    }

    private static void CollectLocalTypeNames(TS.Node node, HashSet<string> names)
    {
        if (TypeDeclarationTypes.Contains(node.Type))
        {
            var typeName = TreeSitterQueryHelpers.TryGetField(node, "name")?.Text;
            if (!string.IsNullOrEmpty(typeName))
            {
                names.Add(typeName);
            }
        }

        foreach (var child in node.NamedChildren)
        {
            CollectLocalTypeNames(child, names);
        }
    }

    private static void WalkForReferences(
        TS.Node node,
        string? currentSource,
        HashSet<string> knownTargets,
        HashSet<(string Source, string Target)> seen,
        List<RelationDraft> relations)
    {
        if (TypeDeclarationTypes.Contains(node.Type))
        {
            var typeName = TreeSitterQueryHelpers.TryGetField(node, "name")?.Text;
            var nextSource = typeName is not null ? ComputeQualifiedName(node, typeName).QualifiedName : currentSource;
            foreach (var child in node.NamedChildren)
            {
                // Inheritance targets are emitted as Extends/Implements, not duplicated as References.
                if (child.Type == "base_list")
                {
                    continue;
                }

                WalkForReferences(child, nextSource, knownTargets, seen, relations);
            }

            return;
        }

        if (node.Type is "method_declaration" or "constructor_declaration" or "property_declaration")
        {
            var memberName = TreeSitterQueryHelpers.TryGetField(node, "name")?.Text;
            var nextSource = memberName is not null ? ComputeQualifiedName(node, memberName).QualifiedName : currentSource;

            var typeNode = TreeSitterQueryHelpers.TryGetField(node, "returns")
                ?? TreeSitterQueryHelpers.TryGetField(node, "type");
            if (nextSource is not null && typeNode is not null)
            {
                RecordTypeNodeReferences(typeNode, nextSource, knownTargets, seen, relations);
            }

            foreach (var child in node.NamedChildren)
            {
                WalkForReferences(child, nextSource, knownTargets, seen, relations);
            }

            return;
        }

        if (node.Type == "field_declaration")
        {
            var varDecl = node.NamedChildren.FirstOrDefault(c => c.Type == "variable_declaration");
            var typeNode = varDecl is not null ? TreeSitterQueryHelpers.TryGetField(varDecl, "type") : null;
            if (varDecl is not null && typeNode is not null)
            {
                foreach (var declarator in varDecl.NamedChildren.Where(c => c.Type == "variable_declarator"))
                {
                    var fieldName = TreeSitterQueryHelpers.TryGetField(declarator, "name")?.Text;
                    if (fieldName is null)
                    {
                        continue;
                    }

                    var fieldSource = ComputeQualifiedName(node, fieldName).QualifiedName;
                    RecordTypeNodeReferences(typeNode, fieldSource, knownTargets, seen, relations);
                }
            }

            return;
        }

        if (currentSource is not null && node.Type is "parameter" or "object_creation_expression" or "typeof_expression" or "cast_expression")
        {
            var typeNode = TreeSitterQueryHelpers.TryGetField(node, "type")
                ?? (node.Type == "typeof_expression" ? node.FirstNamedChild : null);
            if (typeNode is not null)
            {
                RecordTypeNodeReferences(typeNode, currentSource, knownTargets, seen, relations);
            }
        }

        foreach (var child in node.NamedChildren)
        {
            WalkForReferences(child, currentSource, knownTargets, seen, relations);
        }
    }

    private static void RecordTypeNodeReferences(
        TS.Node typeNode,
        string sourceQualifiedName,
        HashSet<string> knownTargets,
        HashSet<(string Source, string Target)> seen,
        List<RelationDraft> relations)
    {
        switch (typeNode.Type)
        {
            case "identifier":
                TryAddReference(typeNode.Text, sourceQualifiedName, knownTargets, seen, relations);
                return;

            case "qualified_name":
                var right = TreeSitterQueryHelpers.TryGetField(typeNode, "name")?.Text;
                if (!string.IsNullOrEmpty(right))
                {
                    TryAddReference(right, sourceQualifiedName, knownTargets, seen, relations);
                }
                else
                {
                    var text = typeNode.Text;
                    var dot = text.LastIndexOf('.');
                    TryAddReference(dot >= 0 ? text[(dot + 1)..] : text, sourceQualifiedName, knownTargets, seen, relations);
                }

                return;

            case "generic_name":
                var genericIdent = typeNode.FirstNamedChild;
                if (genericIdent is not null)
                {
                    TryAddReference(genericIdent.Text, sourceQualifiedName, knownTargets, seen, relations);
                }

                var argList = typeNode.NamedChildren.FirstOrDefault(c => c.Type == "type_argument_list");
                if (argList is not null)
                {
                    foreach (var arg in argList.NamedChildren)
                    {
                        RecordTypeNodeReferences(arg, sourceQualifiedName, knownTargets, seen, relations);
                    }
                }

                return;

            default:
                foreach (var child in typeNode.NamedChildren)
                {
                    RecordTypeNodeReferences(child, sourceQualifiedName, knownTargets, seen, relations);
                }

                return;
        }
    }

    private static void TryAddReference(
        string targetName,
        string sourceQualifiedName,
        HashSet<string> knownTargets,
        HashSet<(string Source, string Target)> seen,
        List<RelationDraft> relations)
    {
        if (string.IsNullOrEmpty(targetName) || !knownTargets.Contains(targetName))
        {
            return;
        }

        if (seen.Add((sourceQualifiedName, targetName)))
        {
            relations.Add(new RelationDraft(sourceQualifiedName, targetName, RelationKind.References));
        }
    }

    private static void AddTestRelations(
        TS.Node root,
        List<RelationDraft> relations,
        HashSet<(string Source, string Callee)> recordedCalls,
        HashSet<(string Source, string Target)> recordedReferences)
    {
        if (recordedCalls.Count == 0 && recordedReferences.Count == 0)
        {
            return;
        }

        var testMethodQualifiedNames = new HashSet<string>(StringComparer.Ordinal);
        var localTargetsByTestType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        CollectTestMethods(root, enclosingTypeQualifiedName: null, enclosingTypeShortName: null, testMethodQualifiedNames, localTargetsByTestType);

        if (testMethodQualifiedNames.Count == 0)
        {
            return;
        }

        var seenTests = new HashSet<(string Source, string Target)>();
        foreach (var testMethod in testMethodQualifiedNames)
        {
            var lastDot = testMethod.LastIndexOf('.');
            var owningType = lastDot > 0 ? testMethod[..lastDot] : string.Empty;
            localTargetsByTestType.TryGetValue(owningType, out var sameClassMembers);

            foreach (var (source, callee) in recordedCalls)
            {
                if (string.Equals(source, testMethod, StringComparison.Ordinal)
                    && (sameClassMembers is null || !sameClassMembers.Contains(callee))
                    && seenTests.Add((testMethod, callee)))
                {
                    relations.Add(new RelationDraft(testMethod, callee, RelationKind.Tests));
                }
            }

            foreach (var (source, target) in recordedReferences)
            {
                if (string.Equals(source, testMethod, StringComparison.Ordinal)
                    && (sameClassMembers is null || !sameClassMembers.Contains(target))
                    && seenTests.Add((testMethod, target)))
                {
                    relations.Add(new RelationDraft(testMethod, target, RelationKind.Tests));
                }
            }
        }
    }

    private static void CollectTestMethods(
        TS.Node node,
        string? enclosingTypeQualifiedName,
        string? enclosingTypeShortName,
        HashSet<string> testMethods,
        Dictionary<string, HashSet<string>> localMembersByType)
    {
        if (TypeDeclarationTypes.Contains(node.Type))
        {
            var shortName = TreeSitterQueryHelpers.TryGetField(node, "name")?.Text;
            var qualName = shortName is not null ? ComputeQualifiedName(node, shortName).QualifiedName : enclosingTypeQualifiedName;
            if (qualName is not null && shortName is not null)
            {
                if (!localMembersByType.TryGetValue(qualName, out var members))
                {
                    members = new HashSet<string>(StringComparer.Ordinal);
                    localMembersByType[qualName] = members;
                }

                members.Add(shortName);
            }

            foreach (var child in node.NamedChildren)
            {
                CollectTestMethods(child, qualName, shortName ?? enclosingTypeShortName, testMethods, localMembersByType);
            }

            return;
        }

        if (node.Type is "method_declaration" or "constructor_declaration" or "property_declaration"
            && enclosingTypeQualifiedName is not null)
        {
            var name = TreeSitterQueryHelpers.TryGetField(node, "name")?.Text;
            if (name is not null)
            {
                if (localMembersByType.TryGetValue(enclosingTypeQualifiedName, out var members))
                {
                    members.Add(name);
                }

                if (node.Type == "method_declaration" && IsTestMethod(node, enclosingTypeShortName))
                {
                    testMethods.Add(ComputeQualifiedName(node, name).QualifiedName);
                }
            }

            return;
        }

        foreach (var child in node.NamedChildren)
        {
            CollectTestMethods(child, enclosingTypeQualifiedName, enclosingTypeShortName, testMethods, localMembersByType);
        }
    }

    private static bool IsTestMethod(TS.Node methodNode, string? enclosingTypeShortName)
    {
        foreach (var child in methodNode.NamedChildren)
        {
            if (child.Type != "attribute_list")
            {
                continue;
            }

            foreach (var attr in child.NamedChildren.Where(c => c.Type == "attribute"))
            {
                var nameNode = TreeSitterQueryHelpers.TryGetField(attr, "name");
                var raw = nameNode?.Text;
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                var dot = raw.LastIndexOf('.');
                var shortName = dot >= 0 ? raw[(dot + 1)..] : raw;
                if (shortName.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    shortName = shortName[..^"Attribute".Length];
                }

                if (TestAttributeNames.Contains(shortName))
                {
                    return true;
                }
            }
        }

        var isTestClass = enclosingTypeShortName is not null
            && (enclosingTypeShortName.EndsWith("Test", StringComparison.Ordinal)
                || enclosingTypeShortName.EndsWith("Tests", StringComparison.Ordinal));
        if (!isTestClass)
        {
            return false;
        }

        return methodNode.NamedChildren.Any(c => c.Type == "modifier" && c.Text == "public");
    }
}
