using Context.Indexing.Abstractions;
using Context.Shared;

namespace Context.Indexing;

/// <summary>
/// SHA-256 <see cref="IContentHasher"/>, delegating to the shared <see cref="Hashing"/> helper
/// so every project derives content hashes the same way.
/// </summary>
public sealed class FileContentHasher : IContentHasher
{
    public string HashFile(string absolutePath) => Hashing.HashBytes(File.ReadAllBytes(absolutePath));

    public string HashText(string text) => Hashing.HashText(text);
}
