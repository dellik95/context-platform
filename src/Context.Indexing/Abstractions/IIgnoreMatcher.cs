namespace Context.Indexing.Abstractions;

/// <summary>
/// Decides whether a project-relative path should be excluded from scanning, parsing,
/// graph building, chunking, embedding and search (see CONTRACTS.md §3 / FR-INDEX-002/003 —
/// ignore rules must apply consistently across the whole pipeline, so every stage should
/// route through the same <see cref="IIgnoreMatcher"/> instance for a given project).
/// </summary>
public interface IIgnoreMatcher
{
    /// <param name="relativePath">
    /// A path relative to the project root, using either '/' or '\' as separator.
    /// May refer to a file or a directory.
    /// </param>
    bool IsIgnored(string relativePath);
}
