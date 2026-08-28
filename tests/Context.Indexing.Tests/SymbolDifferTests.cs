using Context.Domain.Enums;
using Context.Indexing;
using Context.Indexing.Abstractions;
using FluentAssertions;

namespace Context.Indexing.Tests;

public sealed class SymbolDifferTests
{
    private static SymbolDraft MakeSymbol(
        string name,
        string qualifiedName,
        string bodyText,
        SymbolKind kind = SymbolKind.Method,
        int startLine = 1,
        int endLine = 5,
        string? signatureText = null,
        string? docComment = null,
        string? parentQualifiedName = null) =>
        new(
            Name: name,
            QualifiedName: qualifiedName,
            Kind: kind,
            StartLine: startLine,
            EndLine: endLine,
            SignatureText: signatureText ?? $"void {name}()",
            BodyText: bodyText,
            DocComment: docComment,
            ParentQualifiedName: parentQualifiedName);

    [Fact]
    public void Diff_SymbolOnlyInCurrent_IsClassifiedAsAdded()
    {
        var previous = Array.Empty<SymbolDraft>();
        var current = new[] { MakeSymbol("Foo", "NS.Foo", "return 1;") };

        var result = SymbolDiffer.Diff(previous, current);

        result.Added.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Foo");
        result.Removed.Should().BeEmpty();
        result.Changed.Should().BeEmpty();
        result.Unchanged.Should().BeEmpty();
    }

    [Fact]
    public void Diff_SymbolOnlyInPrevious_IsClassifiedAsRemoved()
    {
        var previous = new[] { MakeSymbol("Foo", "NS.Foo", "return 1;") };
        var current = Array.Empty<SymbolDraft>();

        var result = SymbolDiffer.Diff(previous, current);

        result.Removed.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Foo");
        result.Added.Should().BeEmpty();
        result.Changed.Should().BeEmpty();
        result.Unchanged.Should().BeEmpty();
    }

    [Fact]
    public void Diff_SameQualifiedNameAndSameBodyText_IsClassifiedAsUnchanged()
    {
        var previous = new[] { MakeSymbol("Foo", "NS.Foo", "return 1;", startLine: 10, endLine: 12) };
        // Same body, but different line numbers / signature text — should still be Unchanged,
        // since SymbolDiffer compares by BodyText hash only.
        var current = new[] { MakeSymbol("Foo", "NS.Foo", "return 1;", startLine: 20, endLine: 22, signatureText: "void Foo(int x)") };

        var result = SymbolDiffer.Diff(previous, current);

        result.Unchanged.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Foo");
        result.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
        result.Changed.Should().BeEmpty();
    }

    [Fact]
    public void Diff_SameQualifiedNameDifferentBodyText_IsClassifiedAsChanged()
    {
        var previous = new[] { MakeSymbol("Foo", "NS.Foo", "return 1;") };
        var current = new[] { MakeSymbol("Foo", "NS.Foo", "return 2;") };

        var result = SymbolDiffer.Diff(previous, current);

        result.Changed.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Foo");
        result.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
        result.Unchanged.Should().BeEmpty();
    }

    [Fact]
    public void Diff_MixedAddedRemovedChangedUnchanged_ClassifiesEachIndependently()
    {
        var previous = new[]
        {
            MakeSymbol("Alpha", "NS.Alpha", "alpha-old"),   // will change
            MakeSymbol("Beta", "NS.Beta", "beta-body"),      // will be unchanged
            MakeSymbol("Gamma", "NS.Gamma", "gamma-body"),   // will be removed
        };
        var current = new[]
        {
            MakeSymbol("Alpha", "NS.Alpha", "alpha-new"),
            MakeSymbol("Beta", "NS.Beta", "beta-body"),
            MakeSymbol("Delta", "NS.Delta", "delta-body"),   // newly added
        };

        var result = SymbolDiffer.Diff(previous, current);

        result.Added.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Delta");
        result.Removed.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Gamma");
        result.Changed.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Alpha");
        result.Unchanged.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Beta");
    }

    [Fact]
    public void Diff_BothEmpty_ReturnsAllEmptyBuckets()
    {
        var result = SymbolDiffer.Diff(Array.Empty<SymbolDraft>(), Array.Empty<SymbolDraft>());

        result.Added.Should().BeEmpty();
        result.Removed.Should().BeEmpty();
        result.Changed.Should().BeEmpty();
        result.Unchanged.Should().BeEmpty();
    }

    [Fact]
    public void Diff_DuplicateQualifiedNameWithinPrevious_LastOneWinsAsTheComparisonBaseline()
    {
        var previous = new[]
        {
            MakeSymbol("Foo", "NS.Foo", "first-body"),
            MakeSymbol("Foo", "NS.Foo", "second-body"),
        };
        var current = new[] { MakeSymbol("Foo", "NS.Foo", "second-body") };

        var result = SymbolDiffer.Diff(previous, current);

        // The later duplicate ("second-body") is what's kept as the previous snapshot, so an
        // identical current body is Unchanged rather than Changed.
        result.Unchanged.Should().ContainSingle();
        result.Changed.Should().BeEmpty();
    }

    [Fact]
    public void Diff_DuplicateQualifiedNameWithinCurrent_LastOneWins()
    {
        var previous = Array.Empty<SymbolDraft>();
        var current = new[]
        {
            MakeSymbol("Foo", "NS.Foo", "first-body"),
            MakeSymbol("Foo", "NS.Foo", "second-body"),
        };

        var result = SymbolDiffer.Diff(previous, current);

        result.Added.Should().ContainSingle();
        result.Added[0].BodyText.Should().Be("second-body");
    }

    [Fact]
    public void Diff_QualifiedNameComparisonIsOrdinal_CaseDiffersMeansDifferentSymbol()
    {
        var previous = new[] { MakeSymbol("Foo", "NS.Foo", "body") };
        var current = new[] { MakeSymbol("foo", "NS.foo", "body") };

        var result = SymbolDiffer.Diff(previous, current);

        // Different casing means a different qualified name under ordinal comparison,
        // so this is an add + a remove, not an unchanged match.
        result.Added.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.foo");
        result.Removed.Should().ContainSingle().Which.QualifiedName.Should().Be("NS.Foo");
        result.Unchanged.Should().BeEmpty();
    }

    [Fact]
    public void Diff_DifferentKindSameQualifiedNameAndBody_StillCountsAsUnchanged()
    {
        // SymbolDiffer only keys on QualifiedName + BodyText hash; Kind changes are not
        // detected by this differ (documented behavior, not a bug in this differ's scope).
        var previous = new[] { MakeSymbol("Foo", "NS.Foo", "body", kind: SymbolKind.Class) };
        var current = new[] { MakeSymbol("Foo", "NS.Foo", "body", kind: SymbolKind.Interface) };

        var result = SymbolDiffer.Diff(previous, current);

        result.Unchanged.Should().ContainSingle();
        result.Changed.Should().BeEmpty();
    }
}
