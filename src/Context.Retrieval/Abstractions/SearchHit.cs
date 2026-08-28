namespace Context.Retrieval.Abstractions;

/// <summary>
/// One ranked result from <see cref="ISearchEngine"/>. <see cref="NodeId"/> identifies the
/// underlying entity — a Symbol id, File id, or Chunk id (in that preference order) for hits
/// sourced from lexical/vector search, or a graph node's <c>RefId</c>/<c>Id</c> for hits reached
/// purely through graph expansion. <see cref="Kind"/> is a free-form label ("Symbol", "File",
/// "Chunk", or a <c>GraphNodeKind</c> name) rather than a strong enum, since hits can originate
/// from either side of the seam.
/// </summary>
public record SearchHit(Guid NodeId, string Kind, string Label, double Score, string? Snippet);
