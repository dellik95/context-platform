namespace Context.Indexing.Abstractions;

/// <summary>A single non-ignored, non-binary file discovered by an <see cref="IFileScanner"/>.</summary>
public record ScannedFile(string RelativePath, string AbsolutePath, long SizeBytes);
