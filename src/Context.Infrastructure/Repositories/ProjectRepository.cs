using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class ProjectRepository(ContextDbContext db) : IProjectRepository
{
    public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken ct) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.RootPath == rootPath, ct);

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct) =>
        await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<Project> AddAsync(Project project, CancellationToken ct)
    {
        if (project.Id == Guid.Empty)
        {
            project.Id = Guid.NewGuid();
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return project;
    }

    public async Task UpdateAsync(Project project, CancellationToken ct)
    {
        db.Projects.Update(project);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == id, ct))
        {
            return false;
        }

        // A plain Remove(project) + SaveChanges is NOT enough here, even though every table below
        // has an OnDelete(Cascade) FK straight to Project (or transitively via File/Session/Memory):
        // several of those same tables are ALSO the target of a *different*, Restrict-on-delete FK
        // from a table that cascades from Project through a SEPARATE path --
        //   - Chunk.SymbolId -> Symbol (Restrict), while Chunk.FileId -> File and Symbol.FileId ->
        //     File both cascade from the SAME File row -- SQLite gives no ordering guarantee that
        //     Chunk is cascade-deleted before Symbol, so a live Symbol can still have a Chunk
        //     pointing at it when SQLite tries to delete it, throwing "FOREIGN KEY constraint
        //     failed" (confirmed live against a real reindexed project -- this is not theoretical).
        //   - GraphEdge.SourceNodeId/TargetNodeId/OwningFileId and GraphNode.OwningFileId -> File
        //     (all Restrict), while GraphEdge/GraphNode/File all cascade from Project independently.
        //   - Memory.CreatedFromSessionId -> Session (Restrict) and Memory.SupersededById -> Memory
        //     itself (Restrict, self-referencing), while Memory/Session both cascade from Project
        //     independently.
        //   - Symbol.ParentSymbolId -> Symbol itself (Restrict, self-referencing) -- e.g. a
        //     class/method pair from the SAME file cascades from that one File row simultaneously,
        //     so the child method's ParentSymbolId can still point at the live class Symbol when
        //     SQLite tries to delete it. Confirmed live: a project with real class+method symbols
        //     hit exactly this after the Chunk/Symbol fix above resolved the first failure.
        //   - SymbolRelation.SourceSymbolId/TargetSymbolId -> Symbol (Restrict) -- unused/always
        //     empty today (see UpdateGraphJobHandler's remarks), but still part of the schema.
        // IndexingPipeline.IndexParsedFileAsync hits the exact same Chunk/Symbol hazard on a single
        // file and documents its own fix as "clear chunks -> replace symbols -> insert chunks";
        // this generalizes that same ordering to a whole project, deleting each Restrict-blocking
        // table explicitly BEFORE letting Project's own cascade handle everything else. Wrapped in
        // one transaction so a failure partway through can't leave the project half-deleted.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var projectSymbolIds = db.Files.Where(f => f.ProjectId == id).Join(db.Symbols, f => f.Id, s => s.FileId, (f, s) => s.Id);

        await db.SymbolRelations
            .Where(r => projectSymbolIds.Contains(r.SourceSymbolId) || projectSymbolIds.Contains(r.TargetSymbolId))
            .ExecuteDeleteAsync(ct);

        await db.GraphEdges.Where(e => e.ProjectId == id).ExecuteDeleteAsync(ct);
        await db.GraphNodes.Where(n => n.ProjectId == id).ExecuteDeleteAsync(ct);

        await db.Chunks.Where(c => db.Files.Any(f => f.Id == c.FileId && f.ProjectId == id)).ExecuteDeleteAsync(ct);

        // Null out the self-referencing parent link before Project's cascade reaches Symbol (via
        // File), so a parent symbol (e.g. a class) with children (its methods) in the SAME file
        // doesn't block on ParentSymbolId when SQLite deletes them in whatever order it picks.
        await db.Symbols.Where(s => db.Files.Any(f => f.Id == s.FileId && f.ProjectId == id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.ParentSymbolId, (Guid?)null), ct);

        // Null out the self-referencing supersession link before deleting Memories, so one memory
        // in this project superseding another doesn't block the superseded one's deletion.
        await db.Memories.Where(m => m.ProjectId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.SupersededById, (Guid?)null), ct);
        await db.Memories.Where(m => m.ProjectId == id).ExecuteDeleteAsync(ct);

        // Everything else this project owns cascades automatically from here: File -> Symbol/
        // FileVersion (their competing Restrict FKs are already cleared above), Session ->
        // SessionEvent/SessionSummary (Memory's competing Restrict FK is already cleared above),
        // ClaudeIntegration, and File/Session/Memory/GraphNode/GraphEdge/ClaudeIntegration
        // themselves, all via their own direct Cascade FK to Project.
        await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return true;
    }
}
