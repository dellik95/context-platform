using TS = TreeSitter;

namespace Context.Indexing.Parsers.TreeSitter;

/// <summary>
/// The object boxed inside <see cref="Context.Indexing.Abstractions.ParsedAst.Tree"/> for every
/// tree-sitter-backed <see cref="Context.Indexing.Abstractions.ILanguageParser"/> implementation.
/// Bundles the native <see cref="TS.Tree"/> produced by a call to <see cref="TS.Parser.Parse(string)"/>
/// together with the <see cref="TS.Parser"/> instance that produced it: both wrap unmanaged
/// tree-sitter resources and must be disposed, and are kept alive as a pair for the lifetime of
/// this object rather than disposing the parser eagerly, in case a future tree-sitter version
/// ties a tree's validity to its owning parser outliving it.
/// </summary>
/// <remarks>
/// Every call site in this codebase does exactly one <c>Parse</c> followed by exactly one
/// <c>ExtractSymbols</c> or <c>ExtractRelations</c> call before discarding the <c>ParsedAst</c>
/// (see <c>IndexingPipeline.IndexParsedFileAsync</c> and
/// <c>UpdateGraphJobHandler.AddRelationEdgesAsync</c>) — never both extraction methods on the same
/// instance. It is therefore safe for <c>ExtractSymbols</c>/<c>ExtractRelations</c> to
/// <see cref="Dispose"/> this as the very last thing they do before returning; callers must not
/// reuse a <see cref="Context.Indexing.Abstractions.ParsedAst"/> after either extraction method has
/// run on it.
/// </remarks>
internal sealed class TreeSitterParsedTree(TS.Parser parser, TS.Tree tree) : IDisposable
{
    private readonly TS.Parser _parser = parser;
    private bool _disposed;

    public TS.Tree Tree { get; } = tree;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Tree.Dispose();
        _parser.Dispose();
    }
}
