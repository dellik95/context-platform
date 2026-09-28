namespace Context.Memory.Abstractions;

/// <summary>Checks the file paths and symbols a memory claims against the project's code index.
/// Implemented by Context.Application over the indexed files and symbols.</summary>
public interface ICodeVerifier
{
    /// <summary>
    /// <see cref="CodeCheckResult.Verified"/> when a path or symbol is found and nothing is refuted;
    /// <see cref="CodeCheckResult.Refuted"/> when a path is missing from a directory the index does cover
    /// and nothing is found; <see cref="CodeCheckResult.Unknown"/> otherwise. Absence alone never refutes.
    /// </summary>
    Task<CodeCheck> CheckAsync(Guid projectId, IReadOnlyCollection<string> paths, IReadOnlyCollection<string> symbols, CancellationToken ct);
}

public enum CodeCheckResult
{
    Unknown,
    Verified,
    Refuted,
}

public sealed record CodeCheck(CodeCheckResult Result, string? Note)
{
    public static CodeCheck Unknown { get; } = new(CodeCheckResult.Unknown, null);
}
