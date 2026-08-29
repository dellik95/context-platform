using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Indexing.Parsers.TreeSitter;
using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class TreeSitterTypeScriptParserTests
{
    // Covers: a named import, two interfaces (one extending the other — TS-specific
    // extends_type_clause), a class implementing an interface and a derived class extending it
    // (bare identifier extends target), a private field, a constructor with a TS parameter
    // property, JSDoc block-comment doc-comments, member-expression calls ("this.foo()") being
    // deliberately excluded from Calls, a bare-identifier call between two module-level functions,
    // an arrow-function-as-const ("varfn"), and a mix of exported/non-exported top-level symbols
    // (only the exported ones should produce Exports relations).
    private const string Source = """
        import { helper } from "./helper";

        export interface Greeter {
          greet(name: string): string;
        }

        export interface NamedGreeter extends Greeter {
          name: string;
        }

        /** A base widget class. */
        export class Widget implements Greeter {
          private count: number = 0;

          constructor(private label: string) {}

          greet(name: string): string {
            return `Hello ${name}`;
          }

          increment(): void {
            this.count++;
            this.log();
          }

          private log(): void {
            console.log(this.count);
          }
        }

        export class Button extends Widget {
          onClick(): void {
            this.increment();
          }
        }

        function helperFn(): number {
          return 1;
        }

        export function caller(): number {
          return helperFn() + 1;
        }

        export const makeGreeting = (name: string): string => {
          return `Hi ${name}`;
        };
        """;

    private static IReadOnlyList<SymbolDraft> ExtractSymbols(TreeSitterTypeScriptParser parser) =>
        parser.ExtractSymbols(parser.Parse(Source));

    private static IReadOnlyList<RelationDraft> ExtractRelations(TreeSitterTypeScriptParser parser) =>
        parser.ExtractRelations(parser.Parse(Source));

    [Fact]
    public void SupportedExtensions_IsOnlyTs()
    {
        using var parser = new TreeSitterTypeScriptParser();

        parser.SupportedExtensions.Should().BeEquivalentTo(new[] { ".ts" });
    }

    [Fact]
    public void ExtractSymbols_InterfacesClassesAndFunctions_HaveExpectedKindsAndQualifiedNames()
    {
        using var parser = new TreeSitterTypeScriptParser();

        var symbols = ExtractSymbols(parser);

        symbols.Should().HaveCount(13);

        symbols.Should().ContainSingle(s => s.Name == "Greeter" && s.Kind == SymbolKind.Interface
            && s.QualifiedName == "Greeter" && s.ParentQualifiedName == null);
        symbols.Should().ContainSingle(s => s.Name == "NamedGreeter" && s.Kind == SymbolKind.Interface
            && s.QualifiedName == "NamedGreeter");

        symbols.Should().ContainSingle(s => s.Name == "Widget" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Widget" && s.DocComment == "/** A base widget class. */");
        symbols.Should().ContainSingle(s => s.Name == "Button" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Button");

        // Module-level plain function.
        symbols.Should().ContainSingle(s => s.Name == "helperFn" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "helperFn" && s.ParentQualifiedName == null);
        symbols.Should().ContainSingle(s => s.Name == "caller" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "caller");

        // Arrow-function-as-const ("varfn") is reported as a Function, same as a plain declaration.
        symbols.Should().ContainSingle(s => s.Name == "makeGreeting" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "makeGreeting");
    }

    [Fact]
    public void ExtractSymbols_ClassMembers_AreNestedUnderTheirClass()
    {
        using var parser = new TreeSitterTypeScriptParser();

        var symbols = ExtractSymbols(parser);

        symbols.Should().ContainSingle(s => s.Name == "count" && s.Kind == SymbolKind.Field
            && s.QualifiedName == "Widget.count" && s.ParentQualifiedName == "Widget");
        symbols.Should().ContainSingle(s => s.Name == "constructor" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Widget.constructor");
        symbols.Should().ContainSingle(s => s.Name == "greet" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Widget.greet");
        symbols.Should().ContainSingle(s => s.Name == "increment" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Widget.increment");
        symbols.Should().ContainSingle(s => s.Name == "log" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Widget.log");
        symbols.Should().ContainSingle(s => s.Name == "onClick" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Button.onClick" && s.ParentQualifiedName == "Button");
    }

    [Fact]
    public void ExtractRelations_ClassHierarchy_CoversExtendsImplementsAndInterfaceExtends()
    {
        using var parser = new TreeSitterTypeScriptParser();

        var relations = ExtractRelations(parser);

        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Button" && r.TargetName == "Widget" && r.Kind == RelationKind.Extends);
        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Widget" && r.TargetName == "Greeter" && r.Kind == RelationKind.Implements);
        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "NamedGreeter" && r.TargetName == "Greeter" && r.Kind == RelationKind.Extends);
    }

    [Fact]
    public void ExtractRelations_ImportStatement_ProducesTopLevelImportRelation()
    {
        using var parser = new TreeSitterTypeScriptParser();

        var relations = ExtractRelations(parser);

        relations.Should().ContainSingle(r =>
            r.Kind == RelationKind.Imports && r.SourceQualifiedName == "" && r.TargetName == "./helper");
    }

    [Fact]
    public void ExtractRelations_OnlyExportedTopLevelSymbols_ProduceExportsRelations()
    {
        using var parser = new TreeSitterTypeScriptParser();

        var relations = ExtractRelations(parser);

        var exports = relations.Where(r => r.Kind == RelationKind.Exports).ToList();

        // Reflexive self-edges for every exported symbol; helperFn is deliberately NOT exported.
        exports.Should().HaveCount(6);
        exports.Select(r => r.SourceQualifiedName).Should().BeEquivalentTo(
            new[] { "Greeter", "NamedGreeter", "Widget", "Button", "caller", "makeGreeting" });
        exports.Should().OnlyContain(r => r.SourceQualifiedName == r.TargetName);
        exports.Should().NotContain(r => r.SourceQualifiedName == "helperFn");
    }

    [Fact]
    public void ExtractRelations_MemberExpressionCallsAreExcluded_OnlyBareIdentifierCallCounted()
    {
        using var parser = new TreeSitterTypeScriptParser();

        var relations = ExtractRelations(parser);

        var calls = relations.Where(r => r.Kind == RelationKind.Calls).ToList();

        // "this.increment()" and "this.log()" are member-expression calls, deliberately excluded.
        calls.Should().ContainSingle();
        calls[0].SourceQualifiedName.Should().Be("caller");
        calls[0].TargetName.Should().Be("helperFn");
    }
}
