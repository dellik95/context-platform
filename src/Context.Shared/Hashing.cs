using System.Security.Cryptography;
using System.Text;

namespace Context.Shared;

/// <summary>
/// Generic SHA-256 content-hashing helper shared across the platform (content hashes
/// for files, chunks, symbol signatures/bodies, embedding cache keys, etc.).
/// Reused by Context.Indexing's <c>IContentHasher</c> implementations rather than
/// duplicating hashing logic there.
/// </summary>
public static class Hashing
{
    /// <summary>Computes the lowercase hex SHA-256 hash of UTF-8 encoded text.</summary>
    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));

    /// <summary>Computes the lowercase hex SHA-256 hash of raw bytes.</summary>
    public static string HashBytes(byte[] bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }
}
