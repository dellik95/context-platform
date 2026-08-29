namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Query fragments shared by <see cref="TreeSitterTypeScriptParser"/> and
/// <see cref="TreeSitterTsxParser"/> - verified empirically (see the implementation report) that
/// the "typescript" and "tsx" tree-sitter grammars produce identical node/field shapes for every
/// construct these parsers care about (classes, interfaces, methods, fields, heritage clauses),
/// differing only in whether JSX syntax also parses. Plain JavaScript's grammar genuinely differs
/// here (no interfaces at all, a different field-declaration node/field name, and a different,
/// wrapper-less class-heritage shape), so <see cref="TreeSitterJavaScriptParser"/> does not derive
/// from this class - it overrides the same <see cref="TreeSitterJsFamilyParserBase"/> members
/// directly with its own fragments.
/// </summary>
public abstract class TreeSitterTypeScriptLikeParserBase(string languageId) : TreeSitterJsFamilyParserBase(languageId)
{
    // Field: `public_field_definition`, name field `name` (e.g. "private x: number = 1;").
    protected override string FieldQueryFragment { get; } =
        "\n(public_field_definition name: (property_identifier) @field.name) @field.def";

    protected override string InterfaceQueryFragment { get; } =
        "(interface_declaration name: (type_identifier) @interface.name) @interface.def";

    // TS/TSX wrap a class's extended type in an `extends_clause` with a `value` field; the value
    // is already the bare name even when generic (`extends Base<T>` puts `<T>` in a sibling
    // `type_arguments` field, confirmed empirically - see TypeNameOf's remarks for the contrast
    // with `implements`/interface-`extends`, which do wrap generically-parameterized entries).
    protected override string ClassExtendsQuery { get; } =
        "(class_declaration name: (_) @class.name (class_heritage (extends_clause value: (_) @class.extends))) @class.def";

    // The whole `implements_clause` is captured once; TreeSitterJsFamilyParserBase reads its
    // individual interface names off NamedChildren (each a `type_identifier` or, when
    // generically-parameterized, a `generic_type`).
    protected override string? ClassImplementsQuery { get; } =
        "(class_declaration name: (_) @class.name (class_heritage (implements_clause) @class.implements)) @class.def";

    // Likewise, the whole `extends_type_clause` is captured once for an interface extending one or
    // more other interfaces; its children are read the same way as `implements_clause`'s.
    protected override string? InterfaceExtendsQuery { get; } =
        "(interface_declaration name: (type_identifier) @interface.name (extends_type_clause) @interface.extends) @interface.def";
}
