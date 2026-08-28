namespace Context.Indexing.Abstractions;

/// <summary>
/// Opaque parse result handed back to the same <see cref="ILanguageParser"/> that produced it.
/// <see cref="Tree"/> is parser-specific (a tree-sitter node for a real grammar, or the
/// <c>RegexAstTree</c> internal model for <c>RegexHeuristicParser</c>) — callers outside the
/// parser must never inspect it directly. See CONTRACTS.md §3.
/// </summary>
public record ParsedAst(object Tree, string SourceText);
