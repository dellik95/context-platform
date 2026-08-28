namespace Context.Indexing.Abstractions;

/// <summary>
/// A best-effort symbol extracted from source by an <see cref="ILanguageParser"/>, prior to
/// persistence as a <see cref="Context.Domain.Entities.Symbol"/> (which stores hashes rather
/// than raw text). See CONTRACTS.md §3.
/// </summary>
public record SymbolDraft(
    string Name,
    string QualifiedName,
    Context.Domain.Enums.SymbolKind Kind,
    int StartLine,
    int EndLine,
    string SignatureText,
    string BodyText,
    string? DocComment,
    string? ParentQualifiedName);
