namespace Context.Shared;

/// <summary>
/// Lightweight success/failure wrapper used across the platform in place of exceptions
/// for expected failure paths (validation, not-found, provider errors, etc.).
/// </summary>
public readonly record struct Result<T>(bool Success, T? Value, string? Error)
{
    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(string error) => new(false, default, error);
}
