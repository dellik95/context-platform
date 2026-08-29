using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Indexing.Parsers.TreeSitter;
using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class TreeSitterPythonParserTests
{
    // Covers: a plain "import x", a "from x import y" (the import relation targets the *module*
    // name "typing", not "List"), a "from . import x" relative import, a class with a PEP 257
    // docstring, a base class list with a "metaclass=" keyword argument that must be skipped
    // (not treated as a base class), a decorated method (the required tricky case —
    // "@staticmethod" — whose line range/signature must include the decorator line while its
    // qualified name still resolves to a normal nested method), and both an excluded
    // attribute-call ("self.foo()"/"a.speak()") and an excluded class-instantiation call
    // ("Animal(name)" — a class name is never in the known-function-names set) alongside one
    // legitimate bare-identifier function call.
    private const string Source = """"
        import os
        from typing import List
        from . import utils


        class Animal:
            """Base animal class."""

            def __init__(self, name):
                self.name = name

            def speak(self):
                return self.make_sound()

            def make_sound(self):
                return "..."

            @staticmethod
            def kind():
                return "animal"


        class Dog(Animal, metaclass=type):
            def speak(self):
                return "Woof"


        def create_animal(name):
            return Animal(name)


        def use_animal():
            a = create_animal("Rex")
            return a.speak()
        """";

    private static IReadOnlyList<SymbolDraft> ExtractSymbols(TreeSitterPythonParser parser) =>
        parser.ExtractSymbols(parser.Parse(Source));

    private static IReadOnlyList<RelationDraft> ExtractRelations(TreeSitterPythonParser parser) =>
        parser.ExtractRelations(parser.Parse(Source));

    [Fact]
    public void SupportedExtensions_IsOnlyPy()
    {
        using var parser = new TreeSitterPythonParser();

        parser.SupportedExtensions.Should().BeEquivalentTo(new[] { ".py" });
    }

    [Fact]
    public void ExtractSymbols_ClassesAndFunctions_DistinguishMethodFromFunctionByNesting()
    {
        using var parser = new TreeSitterPythonParser();

        var symbols = ExtractSymbols(parser);

        symbols.Should().HaveCount(9);

        symbols.Should().ContainSingle(s => s.Name == "Animal" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Animal" && s.DocComment == "Base animal class.");

        symbols.Should().ContainSingle(s => s.Name == "__init__" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Animal.__init__" && s.ParentQualifiedName == "Animal");
        symbols.Should().ContainSingle(s => s.Name == "speak" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Animal.speak");
        symbols.Should().ContainSingle(s => s.Name == "make_sound" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Animal.make_sound");

        symbols.Should().ContainSingle(s => s.Name == "Dog" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Dog");
        symbols.Should().ContainSingle(s => s.Name == "speak" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Dog.speak" && s.ParentQualifiedName == "Dog");

        // Module-level functions are Function, not Method.
        symbols.Should().ContainSingle(s => s.Name == "create_animal" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "create_animal" && s.ParentQualifiedName == null);
        symbols.Should().ContainSingle(s => s.Name == "use_animal" && s.Kind == SymbolKind.Function
            && s.QualifiedName == "use_animal");
    }

    [Fact]
    public void ExtractSymbols_DecoratedMethod_IncludesDecoratorLineButResolvesNormalQualifiedName()
    {
        using var parser = new TreeSitterPythonParser();

        var symbols = ExtractSymbols(parser);

        var kind = symbols.Single(s => s.Name == "kind" && s.Kind == SymbolKind.Method);
        kind.QualifiedName.Should().Be("Animal.kind");
        kind.ParentQualifiedName.Should().Be("Animal");
        // The decorator line is part of the declaration's own text/line range.
        kind.SignatureText.Should().Contain("@staticmethod");
        kind.SignatureText.Should().Contain("def kind():");
        kind.BodyText.Should().Contain("return \"animal\"");
    }

    [Fact]
    public void ExtractRelations_ClassBaseList_SkipsMetaclassKeywordArgument()
    {
        using var parser = new TreeSitterPythonParser();

        var relations = ExtractRelations(parser);

        var extendsRelations = relations.Where(r => r.Kind == RelationKind.Extends).ToList();
        extendsRelations.Should().ContainSingle();
        extendsRelations[0].SourceQualifiedName.Should().Be("Dog");
        extendsRelations[0].TargetName.Should().Be("Animal");
    }

    [Fact]
    public void ExtractRelations_Imports_TargetTheModuleNameNotTheImportedMember()
    {
        using var parser = new TreeSitterPythonParser();

        var relations = ExtractRelations(parser);

        var imports = relations.Where(r => r.Kind == RelationKind.Imports).ToList();
        imports.Should().HaveCount(3);

        imports.Should().ContainSingle(r => r.SourceQualifiedName == "" && r.TargetName == "os");
        // "from typing import List": the module name is "typing", NOT the imported member "List".
        imports.Should().ContainSingle(r => r.SourceQualifiedName == "" && r.TargetName == "typing");
        // "from . import utils": module_name is the relative-import node itself (just the dot(s)).
        imports.Should().ContainSingle(r => r.SourceQualifiedName == "" && r.TargetName == ".");
    }

    [Fact]
    public void ExtractRelations_AttributeCallsAndClassInstantiationAreExcluded_OnlyBareCallCounted()
    {
        using var parser = new TreeSitterPythonParser();

        var relations = ExtractRelations(parser);

        var calls = relations.Where(r => r.Kind == RelationKind.Calls).ToList();

        // "self.make_sound()" and "a.speak()" are attribute calls (excluded); "Animal(name)" is a
        // class instantiation (never in the known-function-names set, so excluded automatically).
        // Only "create_animal(...)" is a bare-identifier call to a known module-level function.
        calls.Should().ContainSingle();
        calls[0].SourceQualifiedName.Should().Be("use_animal");
        calls[0].TargetName.Should().Be("create_animal");
    }
}
