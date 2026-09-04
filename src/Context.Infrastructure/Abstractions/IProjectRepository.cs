using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken ct);
    Task<Project?> GetByBearerTokenHashAsync(string tokenHash, CancellationToken ct);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct);
    Task<Project> AddAsync(Project project, CancellationToken ct);
    Task UpdateAsync(Project project, CancellationToken ct);

    /// <summary>
    /// Deletes the project row and every row it transitively owns. Most of that is plain FK
    /// cascade configuration (see e.g. <c>FileConfiguration</c>, <c>SessionConfiguration</c>,
    /// <c>MemoryConfiguration</c>, <c>GraphNodeConfiguration</c>, <c>GraphEdgeConfiguration</c>,
    /// <c>ClaudeIntegrationConfiguration</c>) firing at the database level (SQLite's own
    /// <c>ON DELETE CASCADE</c>, enabled via <c>PRAGMA foreign_keys=ON</c> — see
    /// <c>ContextDbContext.SqlitePragmaConnectionInterceptor</c>), but the implementation must
    /// clear a handful of Restrict-on-delete tables (Chunk/SymbolRelation against Symbol,
    /// GraphEdge/GraphNode against File, Memory against Session/itself) in dependency order FIRST
    /// — see <c>ProjectRepository.DeleteAsync</c>'s own remarks for why a plain
    /// <c>Remove(project) + SaveChanges</c> throws "FOREIGN KEY constraint failed" without that.
    /// The one table this does NOT reach at all is <c>IChunkVectorIndex</c> (a separate
    /// sqlite-vec-backed store, not an EF-mapped entity) — callers deleting a project must clean
    /// that up separately.
    /// </summary>
    /// <returns>False if no project with this id exists.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
