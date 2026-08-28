using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure;

/// <summary>
/// EF Core context for the Context Platform's SQLite-backed store. One <see cref="DbSet{TEntity}"/>
/// per entity declared in <c>Context.Domain.Entities</c> (CONTRACTS.md §1).
/// </summary>
public class ContextDbContext : DbContext
{
    public ContextDbContext(DbContextOptions<ContextDbContext> options) : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<DomainFile> Files => Set<DomainFile>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<Symbol> Symbols => Set<Symbol>();
    public DbSet<SymbolRelation> SymbolRelations => Set<SymbolRelation>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<Embedding> Embeddings => Set<Embedding>();
    public DbSet<GraphNode> GraphNodes => Set<GraphNode>();
    public DbSet<GraphEdge> GraphEdges => Set<GraphEdge>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<SessionSummary> SessionSummaries => Set<SessionSummary>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryRelation> MemoryRelations => Set<MemoryRelation>();
    public DbSet<MemoryFile> MemoryFiles => Set<MemoryFile>();
    public DbSet<MemorySymbol> MemorySymbols => Set<MemorySymbol>();
    public DbSet<MemoryEmbedding> MemoryEmbeddings => Set<MemoryEmbedding>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ProviderModel> ProviderModels => Set<ProviderModel>();
    public DbSet<ClaudeIntegration> ClaudeIntegrations => Set<ClaudeIntegration>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // Ensure WAL mode + FK enforcement are applied on every physical connection this
        // context opens, regardless of how the DbContextOptions were built (pooled or not).
        optionsBuilder.AddInterceptors(new SqlitePragmaConnectionInterceptor());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ContextDbContext).Assembly);
    }

    /// <summary>
    /// Applies the pragmas required for correct concurrent SQLite access as soon as a
    /// connection is physically opened: WAL journaling (readers don't block writers) and
    /// foreign-key constraint enforcement (off by default per-connection in SQLite).
    /// </summary>
    private sealed class SqlitePragmaConnectionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
    {
        public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData)
        {
            ApplyPragmas(connection);
        }

        public override async Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        private static void ApplyPragmas(System.Data.Common.DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }

        private static async Task ApplyPragmasAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
        {
            var command = connection.CreateCommand();
            await using var _ = command.ConfigureAwait(false);
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
