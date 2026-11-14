namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// Status response returned when <c>memory_remember</c> or <c>memory_forget</c> queues a write job.
/// </summary>
public sealed record MemoryWriteAccepted(string Status, Guid? JobId);
