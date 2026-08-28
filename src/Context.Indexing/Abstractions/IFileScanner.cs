namespace Context.Indexing.Abstractions;

/// <summary>
/// Recursively enumerates the indexable files under a project root, honoring an
/// <see cref="IIgnoreMatcher"/>. See CONTRACTS.md §3.
/// </summary>
public interface IFileScanner
{
    IAsyncEnumerable<ScannedFile> ScanAsync(string rootPath, IIgnoreMatcher ignore, CancellationToken ct);
}
