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

    /// <summary>Computes the lowercase hex SHA-256 hash of a stream without buffering it in memory.</summary>
    public static string HashStream(Stream stream)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(stream, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Asynchronously computes the lowercase hex SHA-256 hash of a stream.</summary>
    public static async ValueTask<string> HashStreamAsync(Stream stream, CancellationToken ct = default)
    {
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes the base64 SHA-256 hash of a raw bearer token, for storage/lookup of
    /// <c>Project.BearerTokenHash</c> without persisting the token itself.
    /// </summary>
    public static string HashToken(string rawToken)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(rawToken), hash);
        return Convert.ToBase64String(hash);
    }
}
