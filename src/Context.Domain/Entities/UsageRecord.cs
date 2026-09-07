namespace Context.Domain.Entities;

/// <summary>
/// One LLM call's token usage, recorded for cost accounting (BACKLOG-27) - or, when
/// <see cref="SavedTokens"/> is set instead of the LLM-spend fields, one MCP search-tool call's
/// estimated token savings versus reading the referenced files in full (see
/// Context.Dashboard.Server.Mcp.ContextMcpTools). A row is one or the other: spend rows leave
/// <see cref="SavedTokens"/> null; savings rows leave <see cref="Provider"/>/<see cref="Model"/>/
/// <see cref="PromptTokens"/>/<see cref="CompletionTokens"/>/<see cref="TotalTokens"/> null/empty.
/// </summary>
public class UsageRecord
{
    public Guid Id { get; set; }                 // PK
    public Guid ProjectId { get; set; }
    public Guid? SessionId { get; set; }
    public string Purpose { get; set; } = string.Empty;   // e.g. "SessionSummary", "MemoryExtraction", "MemoryConsistencyCheck", "McpContextSearch"
    public string Provider { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>
    /// Estimated tokens saved by an MCP search-tool call returning targeted results instead of the
    /// caller reading every referenced file in full: <c>max(0, fullFileTokens - returnedTokens)</c>,
    /// both estimated with the shared TokenEstimator heuristic. Null for LLM-spend rows. A
    /// counterfactual estimate, not a measured fact - see callers for the exact methodology.
    /// </summary>
    public int? SavedTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
