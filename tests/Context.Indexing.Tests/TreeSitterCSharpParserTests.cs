using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Indexing.Parsers.TreeSitter;
using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class TreeSitterCSharpParserTests
{
    // Covers: file-scoped namespace, an abstract base class + interface (both with a headerless
    // method — no body at all), a derived class implementing an interface and extending a base
    // class (Extends vs. Implements disambiguated by the "IFoo" naming convention), a nested
    // class (qualified-name nesting), a constructor, a private field, an expression-bodied method
    // (also headerless — "=>" is not a "body" field), a generic class with a generic interface in
    // its base list (tricky case for BaseTypeName's generic_name unwrapping), leading "///" doc
    // comments, using directives, and same-file bare-identifier method calls.
    private const string Source = """
        using System;
        using System.Collections.Generic;

        namespace Acme.Shapes;

        /// <summary>Base shape.</summary>
        public abstract class Shape
        {
            public double Area { get; set; }

            public abstract double ComputeArea();
        }

        public interface IShape
        {
            double ComputeArea();
        }

        /// <summary>A circle shape.</summary>
        public class Circle : Shape, IShape
        {
            private double _radius;

            public Circle(double radius)
            {
                _radius = radius;
                Initialize();
            }

            private void Initialize()
            {
                Area = ComputeArea();
            }

            public override double ComputeArea()
            {
                return Math.PI * _radius * _radius;
            }

            public class Metadata
            {
                public string Name { get; set; }
            }
        }

        public class Box<T> : IEquatable<Box<T>>
        {
            public T Value { get; set; }

            public bool Equals(Box<T> other) => true;
        }
        """;

    private static IReadOnlyList<SymbolDraft> ExtractSymbols(TreeSitterCSharpParser parser) =>
        parser.ExtractSymbols(parser.Parse(Source));

    private static IReadOnlyList<RelationDraft> ExtractRelations(TreeSitterCSharpParser parser) =>
        parser.ExtractRelations(parser.Parse(Source));

    [Fact]
    public void SupportedExtensions_IsOnlyCs()
    {
        using var parser = new TreeSitterCSharpParser();

        parser.SupportedExtensions.Should().BeEquivalentTo(new[] { ".cs" });
    }

    [Fact]
    public void ExtractSymbols_TopLevelAndNestedTypes_HaveCorrectKindsAndQualifiedNames()
    {
        using var parser = new TreeSitterCSharpParser();

        var symbols = ExtractSymbols(parser);

        symbols.Should().HaveCount(15);

        symbols.Should().ContainSingle(s => s.Name == "Shape" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Acme.Shapes.Shape" && s.ParentQualifiedName == null
            && s.DocComment == "<summary>Base shape.</summary>");

        symbols.Should().ContainSingle(s => s.Name == "IShape" && s.Kind == SymbolKind.Interface
            && s.QualifiedName == "Acme.Shapes.IShape");

        symbols.Should().ContainSingle(s => s.Name == "Circle" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Acme.Shapes.Circle"
            && s.DocComment == "<summary>A circle shape.</summary>");

        // Nested class: qualified name is dotted through its enclosing type, and its
        // ParentQualifiedName points back at that enclosing type.
        symbols.Should().ContainSingle(s => s.Name == "Metadata" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Acme.Shapes.Circle.Metadata"
            && s.ParentQualifiedName == "Acme.Shapes.Circle");

        symbols.Should().ContainSingle(s => s.Name == "Name" && s.Kind == SymbolKind.Property
            && s.QualifiedName == "Acme.Shapes.Circle.Metadata.Name"
            && s.ParentQualifiedName == "Acme.Shapes.Circle.Metadata");

        symbols.Should().ContainSingle(s => s.Name == "Box" && s.Kind == SymbolKind.Class
            && s.QualifiedName == "Acme.Shapes.Box");
    }

    [Fact]
    public void ExtractSymbols_Members_AreNestedUnderTheirDeclaringType()
    {
        using var parser = new TreeSitterCSharpParser();

        var symbols = ExtractSymbols(parser);

        symbols.Should().ContainSingle(s => s.Name == "Area" && s.Kind == SymbolKind.Property
            && s.QualifiedName == "Acme.Shapes.Shape.Area" && s.ParentQualifiedName == "Acme.Shapes.Shape");

        symbols.Should().ContainSingle(s => s.Name == "_radius" && s.Kind == SymbolKind.Field
            && s.QualifiedName == "Acme.Shapes.Circle._radius" && s.ParentQualifiedName == "Acme.Shapes.Circle");

        // The constructor is a "method_declaration"-shaped node whose own name is the class name.
        symbols.Should().ContainSingle(s => s.Name == "Circle" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Acme.Shapes.Circle.Circle" && s.ParentQualifiedName == "Acme.Shapes.Circle");

        symbols.Should().ContainSingle(s => s.Name == "Initialize" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Acme.Shapes.Circle.Initialize");

        symbols.Should().ContainSingle(s => s.Name == "ComputeArea" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Acme.Shapes.Circle.ComputeArea");

        // Interface method: still extracted as a Method nested under the interface.
        symbols.Should().ContainSingle(s => s.Name == "ComputeArea" && s.Kind == SymbolKind.Method
            && s.QualifiedName == "Acme.Shapes.IShape.ComputeArea");
    }

    [Fact]
    public void ExtractSymbols_HeaderlessDeclarations_HaveWholeDeclarationAsSignatureAndEmptyBody()
    {
        using var parser = new TreeSitterCSharpParser();

        var symbols = ExtractSymbols(parser);

        // Abstract method: no body at all.
        var abstractMethod = symbols.Single(s => s.QualifiedName == "Acme.Shapes.Shape.ComputeArea");
        abstractMethod.SignatureText.Should().Be("public abstract double ComputeArea();");
        abstractMethod.BodyText.Should().BeEmpty();

        // Interface method: no body at all.
        var interfaceMethod = symbols.Single(s => s.QualifiedName == "Acme.Shapes.IShape.ComputeArea");
        interfaceMethod.SignatureText.Should().Be("double ComputeArea();");
        interfaceMethod.BodyText.Should().BeEmpty();

        // Expression-bodied method ("=> true;"): has no "body" field either, so it's headerless too.
        var expressionBodied = symbols.Single(s => s.QualifiedName == "Acme.Shapes.Box.Equals");
        expressionBodied.Kind.Should().Be(SymbolKind.Method);
        expressionBodied.SignatureText.Should().Be("public bool Equals(Box<T> other) => true;");
        expressionBodied.BodyText.Should().BeEmpty();
    }

    [Fact]
    public void ExtractSymbols_RegularMethodWithBlockBody_SplitsSignatureAndBody()
    {
        using var parser = new TreeSitterCSharpParser();

        var symbols = ExtractSymbols(parser);

        var computeArea = symbols.Single(s => s.QualifiedName == "Acme.Shapes.Circle.ComputeArea");
        computeArea.SignatureText.Should().Be("public override double ComputeArea()");
        computeArea.BodyText.Should().Contain("return Math.PI * _radius * _radius;");
    }

    [Fact]
    public void ExtractRelations_ClassHierarchy_DistinguishesExtendsFromImplements()
    {
        using var parser = new TreeSitterCSharpParser();

        var relations = ExtractRelations(parser);

        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Acme.Shapes.Circle" && r.TargetName == "Shape" && r.Kind == RelationKind.Extends);

        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Acme.Shapes.Circle" && r.TargetName == "IShape" && r.Kind == RelationKind.Implements);

        // Tricky case: a generic interface in the base list ("IEquatable<Box<T>>") is a
        // generic_name node — the base type name must be unwrapped down to "IEquatable".
        relations.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Acme.Shapes.Box" && r.TargetName == "IEquatable" && r.Kind == RelationKind.Implements);
    }

    [Fact]
    public void ExtractRelations_UsingDirectives_ProduceTopLevelImportRelations()
    {
        using var parser = new TreeSitterCSharpParser();

        var relations = ExtractRelations(parser);

        var imports = relations.Where(r => r.Kind == RelationKind.Imports).ToList();
        imports.Should().HaveCount(2);
        imports.Should().ContainSingle(r => r.SourceQualifiedName == "" && r.TargetName == "System");
        imports.Should().ContainSingle(r => r.SourceQualifiedName == "" && r.TargetName == "System.Collections.Generic");
    }

    [Fact]
    public void ExtractRelations_BareIdentifierCallsToKnownMethods_ProduceCallsRelations()
    {
        using var parser = new TreeSitterCSharpParser();

        var relations = ExtractRelations(parser);

        var calls = relations.Where(r => r.Kind == RelationKind.Calls).ToList();
        calls.Should().HaveCount(2);
        calls.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Acme.Shapes.Circle.Circle" && r.TargetName == "Initialize");
        calls.Should().ContainSingle(r =>
            r.SourceQualifiedName == "Acme.Shapes.Circle.Initialize" && r.TargetName == "ComputeArea");
    }

    // Regression coverage for the real bug this fix addresses: FormatMemoryNote-style
    // (Application.ContextEngine) and this.Foo()/x.Foo() style calls were invisible to the graph
    // because InvocationsQuery only ever matched a bare-identifier function, never a
    // member_access_expression one (which is what every "this.Foo()"/"x.Foo()"/LINQ call actually
    // is grammar-wise).
    private const string MemberAccessSource = """
        namespace Acme.Widgets;

        public class Formatter
        {
            public string[] FormatAll(string[] items)
            {
                this.Prepare();
                return items.Select(FormatOne).ToArray();
            }

            private void Prepare()
            {
            }

            private static string FormatOne(string item) => item;
        }
        """;

    [Fact]
    public void ExtractRelations_MemberAccessCall_ProducesCallsRelation()
    {
        using var parser = new TreeSitterCSharpParser();

        var relations = parser.ExtractRelations(parser.Parse(MemberAccessSource));

        // "this.Prepare()" — the invocation's function is a member_access_expression, not a bare
        // identifier; this is the "this.Foo()"/"x.Foo()" half of the fix.
        relations.Should().ContainSingle(r =>
            r.Kind == RelationKind.Calls
            && r.SourceQualifiedName == "Acme.Widgets.Formatter.FormatAll"
            && r.TargetName == "Prepare");
    }

    [Fact]
    public void ExtractRelations_MethodGroupReferenceAsArgument_ProducesCallsRelation()
    {
        using var parser = new TreeSitterCSharpParser();

        var relations = parser.ExtractRelations(parser.Parse(MemberAccessSource));

        // ".Select(FormatOne)" — FormatOne is never invoked at all, just named as a bare identifier
        // argument; no invocation_expression wraps it, so this needs the separate
        // MethodGroupReferencesQuery pass, not just a wider InvocationsQuery.
        relations.Should().ContainSingle(r =>
            r.Kind == RelationKind.Calls
            && r.SourceQualifiedName == "Acme.Widgets.Formatter.FormatAll"
            && r.TargetName == "FormatOne");
    }

    [Fact]
    public void ExtractRelations_EventSubscription_ProducesCallsRelation()
    {
        const string source = """
            namespace Acme.Widgets;

            public class Button
            {
                public event System.Action Clicked;

                public void Wire()
                {
                    Clicked += OnClicked;
                }

                private void OnClicked()
                {
                }
            }
            """;
        using var parser = new TreeSitterCSharpParser();

        var relations = parser.ExtractRelations(parser.Parse(source));

        relations.Should().ContainSingle(r =>
            r.Kind == RelationKind.Calls
            && r.SourceQualifiedName == "Acme.Widgets.Button.Wire"
            && r.TargetName == "OnClicked");
    }
}
