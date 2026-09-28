namespace Context.Memory.Abstractions;

/// <param name="ModelKey">The embedding generator's <c>GetProviderModelKey()</c>; vectors with
/// different keys are never compared.</param>
public sealed record MemoryVector(string ModelKey, float[] Vector);
