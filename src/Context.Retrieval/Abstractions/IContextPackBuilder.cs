namespace Context.Retrieval.Abstractions;

/// <summary>
/// Builds a token-budgeted <see cref="ContextPack"/> for a query. See
/// <see cref="Context.Retrieval.ContextPackBuilder"/> for the default implementation, which also
/// exposes richer overloads (accepting historical/memory notes and open issues) not part of this
/// contract-fixed interface — see its remarks.
/// </summary>
public interface IContextPackBuilder
{
    Task<ContextPack> BuildAsync(SearchQuery query, CancellationToken ct);
}
