namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// Real tree-sitter-backed <see cref="Context.Indexing.Abstractions.ILanguageParser"/> for plain
/// JavaScript (grammar id <c>"javascript"</c>, extensions <c>.js</c> and <c>.jsx</c>). Verified
/// empirically that tree-sitter-javascript's own grammar parses JSX elements natively inside a
/// <c>.js</c>-style source sample (a <c>jsx_element</c> with the same shape the "tsx" grammar
/// produces) - so <c>.jsx</c> does not need the "tsx" grammar; it is handled by this same parser and
/// grammar id as <c>.js</c>. No JSX-specific extraction is needed either way: none of
/// classes/interfaces/methods/functions/fields/heritage/imports/exports/calls depend on JSX syntax.
/// </summary>
/// <remarks>
/// Differs from <see cref="TreeSitterTypeScriptLikeParserBase"/> (used by
/// <see cref="TreeSitterTypeScriptParser"/>/<see cref="TreeSitterTsxParser"/>) in exactly the ways
/// plain JavaScript's grammar genuinely differs from TypeScript's: no interfaces at all
/// (<see cref="TreeSitterJsFamilyParserBase.InterfaceQueryFragment"/> is left at its empty
/// default), a field declaration is <c>field_definition</c> with field name <c>property</c> rather
/// than TS's <c>public_field_definition</c>/<c>name</c>, and <c>class_heritage</c> holds the
/// extended expression directly with no <c>extends_clause</c> wrapper (and no <c>implements</c> at
/// all, since JavaScript has no interfaces to implement).
/// </remarks>
public sealed class TreeSitterJavaScriptParser() : TreeSitterJsFamilyParserBase("javascript")
{
    public override IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js", ".jsx" };

    protected override string FieldQueryFragment { get; } =
        "\n(field_definition property: (property_identifier) @field.name) @field.def";

    // No wrapper node at all: `class Foo extends Base {}`'s class_heritage directly contains the
    // extended expression (an `identifier`, a `member_expression` for `extends ns.Base`, or
    // whatever else a dynamic mixin expression like `extends mixin(Base)` produces).
    protected override string ClassExtendsQuery { get; } =
        "(class_declaration name: (_) @class.name (class_heritage (_) @class.extends)) @class.def";

    // ClassImplementsQuery / InterfaceExtendsQuery are left at their base-class `null` default:
    // plain JavaScript has no `implements` keyword and no interfaces.
}
