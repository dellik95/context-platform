using Context.Indexing.Abstractions;
using Context.Shared;

namespace Context.Indexing;

/// <summary>
/// SHA-256 <see cref="IContentHasher"/> and <see cref="IAsyncContentHasher"/>, delegating to the
/// shared <see cref="Hashing"/> helper so every project derives content hashes the same way without
/// reading whole files into memory.
/// </summary>
public sealed class FileContentHasher : IAsyncContentHasher
{
    private const int StreamBufferSize = 64 * 1024;

    public string HashFile(string absolutePath)
    {
        using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            StreamBufferSize,
            FileOptions.SequentialScan);
        return Hashing.HashStream(stream);
    }

    public async ValueTask<string> HashFileAsync(string absolutePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await Hashing.HashStreamAsync(stream, ct).ConfigureAwait(false);
    }

    public string HashText(string text) => Hashing.HashText(text);
}
