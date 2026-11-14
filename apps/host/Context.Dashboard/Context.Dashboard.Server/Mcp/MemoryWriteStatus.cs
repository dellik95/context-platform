namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// Well-known <see cref="MemoryWriteAccepted.Status"/> constants for asynchronous MCP memory writes.
/// </summary>
public static class MemoryWriteStatus
{
    public const string Saving = "saving";
    public const string Deleting = "deleting";
    public const string NotFound = "not_found";
}
