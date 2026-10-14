namespace Context.Retrieval.Abstractions;

/// <summary>A single unified retrieval request — see CONTRACTS.md §6.</summary>
public record SearchQuery(Guid ProjectId, string Text, string[]? Scope, int TokenBudget = 2000, string? SessionKey = null);
