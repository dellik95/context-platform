using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Indexing.Parsers.TreeSitter;
using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class TreeSitterJavaScriptParserTests
{
    // Covers: a named import, a class with a plain-JS class field ("field_definition"/"property" -
    // genuinely different grammar shape than TS's "public_field_definition"/"name"), a constructor,
    // a derived class extending the base directly (no extends_clause wrapper in plain JS), an
    // arrow-function-as-const ("makeDog" — the required tricky case), a bare-identifier call
    // between two module-level functions, a member-expression call ("a.speak()") deliberately
    // excluded from Calls, and a named export clause with an alias ("createAnimal as create").
    private const string Source = """
        import { helper } from "./helper.js";

        class Animal {
          legs = 4;

          constructor(name) {
            this.name = name;
          }

          speak() {
            return `${this.name} makes a noise.`;
          }
        }

        class Dog extends Animal {
          speak() {
            return `${this.name} barks.`;
          }
        }

        function createAnimal(name) {
          return new Animal(name);
        }

        const makeDog = (name) => {
          return new Dog(name);
        };

        function useAnimal() {
          const a = createAnimal("Rex");
          return a.speak();
        }

        export { Dog, createAnimal as create };
        """;

    private static IReadOnlyList<SymbolDraft> ExtractSymbols(TreeSitterJavaScriptParser parser) =>
        parser.ExtractSymbols(parser.Parse(Source));

    private static IReadOnlyList<RelationDraft> ExtractRelations(TreeSitterJavaScriptParser parser) =>
        parser.ExtractRelations(parser.Parse(Source));

    [Fact]
    public void SupportedExtensions_IsJsAndJsx()
    {
        using var parser = new TreeSitterJavaScriptParser();

        parser.SupportedExtensions.Should().BeEquivalentTo(new[] { ".js", ".jsx" });
    }

    [Fact]
    public void ExtractSymbols_ClassesFieldsAndFunctions_HaveExpectedKindsAndQualifiedNames()
    {
        using var parser = new TreeSitterJavaScriptParser();

        var symbols = ExtractSymbols(parser);

        symbols.Should().HaveCount(9);

        symbols.Should().ContainSingle(s => s.Name == "Animal" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Animal");
        symbols.Should().ContainSingle(s => s.Name == "legs" && s.Kind == SymbolKind.Field
            && s.QualifiedName == "Animal.legs" && s.ParentQualifiedName == "Animal");
        symbols.Should().ContainSingle(s => s.Name == "constructor" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Animal.constructor");
        symbols.Should().ContainSingle(s => s.Name == "speak" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Animal.speak");

        symbols.Should().ContainSingle(s => s.Name == "Dog" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Dog");
        symbols.Should().ContainSingle(s => s.Name == "speak" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Dog.speak" && s.ParentQualifiedName == "Dog");

        symbols.Should().ContainSingle(s => s.Name == "createAnimal" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "createAnimal");
        symbols.Should().ContainSingle(s => s.Name == "useAnimal" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "useAnimal");

        // Tricky case: arrow-function-as-const is reported the same as a plain function declaration.
        symbols.Should().ContainSingle(s => s.Name == "makeDog" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "makeDog" && s.ParentQualifiedName == null);
    }

    [Fact]
    public void ExtractRelations_ClassExtends_UsesUnwrappedHeritageWithNoClauseNode()
    {
        using var parser = new TreeSitterJavaScriptParser();

        var relations = ExtractRelations(parser);

        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Dog" && r.TargetName == "Animal" && r.Kind == RelationKind.Extends);
    }

    [Fact]
    public void ExtractRelations_ImportStatement_ProducesTopLevelImportRelation()
    {
        using var parser = new TreeSitterJavaScriptParser();

        var relations = ExtractRelations(parser);

        relations.Should().ContainSingle(r =>
            r.Kind == RelationKind.Imports && r.SourceQualifiedName == "" && r.TargetName == "./helper.js");
    }

    [Fact]
    public void ExtractRelations_NamedExportClauseWithAlias_UsesAliasAsExportedName()
    {
        using var parser = new TreeSitterJavaScriptParser();

        var relations = ExtractRelations(parser);

        var exports = relations.Where(r => r.Kind == RelationKind.Exports).ToList();
        exports.Should().HaveCount(2);

        exports.Should().ContainSingle(r => r.SourceQualifiedName == "Dog" && r.TargetName == "Dog");
        // "createAnimal as create": the local declared name is the RelationDraft's source, but the
        // exported (aliased) name is the target.
        exports.Should().ContainSingle(r => r.SourceQualifiedName == "createAnimal" && r.TargetName == "create");

        // "Animal" is never named in the export clause, so it must not produce an export relation
        // even though it's a known top-level class.
        exports.Should().NotContain(r => r.SourceQualifiedName == "Animal");
    }

    [Fact]
    public void ExtractRelations_OnlyBareIdentifierCallCounted_MemberExpressionCallExcluded()
    {
        using var parser = new TreeSitterJavaScriptParser();

        var relations = ExtractRelations(parser);

        var calls = relations.Where(r => r.Kind == RelationKind.Calls).ToList();

        // "a.speak()" is a member-expression call, deliberately excluded; only the bare
        // "createAnimal(...)" call is reported.
        calls.Should().ContainSingle();
        calls[0].SourceQualifiedName.Should().Be("useAnimal");
        calls[0].TargetName.Should().Be("createAnimal");
    }
}
