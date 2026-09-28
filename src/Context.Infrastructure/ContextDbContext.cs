using Context.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure;

/// <summary>
/// EF Core context for the Context Platform's SQLite-backed store. One <see cref="DbSet{TEntity}"/>
/// per entity declared in <c>Context.Domain.Entities</c>.
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
    public DbSet<SessionSummaryChunk> SessionSummaryChunks => Set<SessionSummaryChunk>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryEmbedding> MemoryEmbeddings => Set<MemoryEmbedding>();
    public DbSet<MemoryEvidence> MemoryEvidence => Set<MemoryEvidence>();
    public DbSet<MemoryEntity> MemoryEntities => Set<MemoryEntity>();
    public DbSet<MemoryConsolidationReport> MemoryConsolidationReports => Set<MemoryConsolidationReport>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ProviderModel> ProviderModels => Set<ProviderModel>();
    public DbSet<ClaudeIntegration> ClaudeIntegrations => Set<ClaudeIntegration>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<HookInvocation> HookInvocations => Set<HookInvocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ContextDbContext).Assembly);
    }

    /// <summary>
    /// Applies the pragmas required for correct concurrent SQLite access as soon as a
    /// connection is physically opened: WAL journaling (readers don't block writers),
    /// foreign-key constraint enforcement (off by default per-connection in SQLite), and a
    /// 10-second busy_timeout so a writer blocked behind another connection's transaction
    /// retries instead of failing immediately with SQLITE_BUSY.
    /// Registered at <see cref="DbContextOptionsBuilder"/> configuration time (see
    /// <c>ServiceCollectionExtensions.AddContextInfrastructure</c>) rather than via an
    /// <c>OnConfiguring</c> override, because EF Core forbids mutating options in
    /// <c>OnConfiguring</c> when the context is registered with <c>AddDbContextPool</c>.
    /// </summary>
    internal sealed class SqlitePragmaConnectionInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
    {
        public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData)
        {
            ApplyPragmas(connection);
        }

        public override async Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        internal const string PragmaCommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";

        private static void ApplyPragmas(System.Data.Common.DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = PragmaCommandText;
            command.ExecuteNonQuery();
        }

        private static async Task ApplyPragmasAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
        {
            var command = connection.CreateCommand();
            await using var _ = command.ConfigureAwait(false);
            command.CommandText = PragmaCommandText;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
