using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Indexing.Parsers.TreeSitter;
using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class TreeSitterTsxParserTests
{
    // Covers: real JSX syntax in both a class method's return statement and an arrow-function
    // component body (confirming JSX doesn't break extraction), a class extending a
    // member-expression base type with a generic type argument ("React.Component<CardProps>" —
    // tricky case: the base name must resolve to the bare "React.Component" member-expression
    // text, not the whole generic instantiation), a default import, JSDoc, and exported symbols.
    private const string Source = """
        import React from "react";

        export interface CardProps {
          title: string;
        }

        /** A simple card component. */
        export class Card extends React.Component<CardProps> {
          render() {
            return <div className="card">{this.props.title}</div>;
          }
        }

        export const Badge = (props: { label: string }) => {
          return <span>{props.label}</span>;
        };
        """;

    private static IReadOnlyList<SymbolDraft> ExtractSymbols(TreeSitterTsxParser parser) =>
        parser.ExtractSymbols(parser.Parse(Source));

    private static IReadOnlyList<RelationDraft> ExtractRelations(TreeSitterTsxParser parser) =>
        parser.ExtractRelations(parser.Parse(Source));

    [Fact]
    public void SupportedExtensions_IsOnlyTsx()
    {
        using var parser = new TreeSitterTsxParser();

        parser.SupportedExtensions.Should().BeEquivalentTo(new[] { ".tsx" });
    }

    [Fact]
    public void ExtractSymbols_ClassAndArrowComponent_AreExtractedDespiteJsxSyntax()
    {
        using var parser = new TreeSitterTsxParser();

        var symbols = ExtractSymbols(parser);

        symbols.Should().HaveCount(4);

        symbols.Should().ContainSingle(s => s.Name == "CardProps" && s.Kind == SymbolKind.Interface
            && s.QualifiedName == "CardProps");

        symbols.Should().ContainSingle(s => s.Name == "Card" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Card" && s.DocComment == "/** A simple card component. */");

        symbols.Should().ContainSingle(s => s.Name == "render" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Card.render" && s.ParentQualifiedName == "Card");

        // Arrow-function-as-const component, same "varfn" convention as plain TypeScript.
        symbols.Should().ContainSingle(s => s.Name == "Badge" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "Badge" && s.ParentQualifiedName == null);
    }

    [Fact]
    public void ExtractRelations_ExtendsMemberExpressionBaseType_ResolvesToBareName()
    {
        using var parser = new TreeSitterTsxParser();

        var relations = ExtractRelations(parser);

        // "React.Component<CardProps>": the extends_clause's "value" field is already the bare
        // "React.Component" member-expression text (generic args live in a sibling field), so no
        // generic-unwrapping is needed here — unlike an implements-position generic_type.
        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Card" && r.TargetName == "React.Component" && r.Kind == RelationKind.Extends);
    }

    [Fact]
    public void ExtractRelations_ImportAndExports_AreProduced()
    {
        using var parser = new TreeSitterTsxParser();

        var relations = ExtractRelations(parser);

        relations.Should().ContainSingle(r =>
            r.Kind == RelationKind.Imports && r.SourceQualifiedName == "" && r.TargetName == "react");

        var exports = relations.Where(r => r.Kind == RelationKind.Exports).ToList();
        exports.Should().HaveCount(3);
        exports.Select(r => r.SourceQualifiedName).Should().BeEquivalentTo(
            new[] { "CardProps", "Card", "Badge" });
    }

    [Fact]
    public void ExtractRelations_NoBareIdentifierCalls_ProducesNoCallsRelations()
    {
        using var parser = new TreeSitterTsxParser();

        var relations = ExtractRelations(parser);

        relations.Should().NotContain(r => r.Kind == RelationKind.Calls);
    }
}
