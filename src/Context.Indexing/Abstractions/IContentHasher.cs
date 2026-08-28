namespace Context.Indexing.Abstractions;

/// <summary>
/// Computes stable content hashes used for file-identity, symbol-body diffing and
/// embedding cache keys. See CONTRACTS.md §3.
/// </summary>
public interface IContentHasher
{
    /// <summary>Hashes the bytes on disk at <paramref name="absolutePath"/>.</summary>
    string HashFile(string absolutePath);

    /// <summary>Hashes UTF-8 encoded text.</summary>
    string HashText(string text);
}
