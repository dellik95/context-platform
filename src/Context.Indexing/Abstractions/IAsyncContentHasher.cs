namespace Context.Indexing.Abstractions;

/// <summary>
/// Optional asynchronous extension to <see cref="IContentHasher"/> for non-blocking streaming file hashing.
/// </summary>
public interface IAsyncContentHasher : IContentHasher
{
    ValueTask<string> HashFileAsync(string absolutePath, CancellationToken ct = default);
}
