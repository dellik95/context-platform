# Context Platform — Cross-Cutting Contracts (authoritative)

This file is the single source of truth for type names, namespaces and signatures
shared across projects. Every implementing agent/dev MUST match these exactly —
do not rename, do not "improve" a signature without updating this file first.

Solution: `ContextPlatform.slnx`. TargetFramework: `net10.0` everywhere (see
`Directory.Build.props`). Central package versions live in `Directory.Packages.props`
— add `<PackageReference Include="X" />` with **no** `Version` attribute.

Project → responsibility map is in `specs/claudy-mem-specs.md` §8/§9 (business spec)
and the published implementation spec artifact. This file only pins **code shapes**.

---

## 1. Context.Domain (`Context.Domain`)

No dependencies on EF Core or any other project. Plain POCOs + enums only.
Namespace: `Context.Domain.Entities`, enums in `Context.Domain.Enums`.

```csharp
namespace Context.Domain.Entities;

public class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string RootPath { get; set; }
    public string? VcsRemote { get; set; }
    public string? ConfigJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class File
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string RelativePath { get; set; }
    public string? Language { get; set; }
    public required string ContentHash { get; set; }   // SHA-256 hex
    public long SizeBytes { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class FileVersion
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public int AstVersion { get; set; }
}

public enum SymbolKind { Class, Interface, Struct, Enum, Function, Method, Property, Field, Variable, Module, Type }

public class Symbol
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public Guid? ParentSymbolId { get; set; }
    public SymbolKind Kind { get; set; }
    public required string Name { get; set; }
    public required string QualifiedName { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public required string SignatureHash { get; set; }
    public required string BodyHash { get; set; }
    public string? DocComment { get; set; }
}

public enum RelationKind { Calls, References, Extends, Implements, Uses, Imports, Exports, Tests, DependsOn }

public class SymbolRelation
{
    public Guid Id { get; set; }
    public Guid SourceSymbolId { get; set; }
    public Guid TargetSymbolId { get; set; }
    public RelationKind Kind { get; set; }
    public bool IsResolved { get; set; }               // false = syntactic guess, true = resolution-pass confirmed
}

public class Chunk
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public Guid? SymbolId { get; set; }
    public required string Text { get; set; }
    public int TokenCount { get; set; }
    public required string ContentHash { get; set; }   // key into Embedding
}

public class Embedding
{
    public required string ContentHash { get; set; }   // PK
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public int Dim { get; set; }
    public required byte[] Vector { get; set; }         // packed float32[]
    public DateTimeOffset CreatedAt { get; set; }
}

public enum GraphNodeKind { Project, Directory, File, Module, Class, Interface, Function, Method, Type, Variable, Package, Test, UnresolvedExternal }

public class GraphNode
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public GraphNodeKind Kind { get; set; }
    public Guid? RefId { get; set; }                    // FileId or SymbolId, null for synthetic nodes
    public required string Label { get; set; }
    public Guid? OwningFileId { get; set; }             // drives incremental invalidation
}

public enum GraphEdgeKind { Contains, Imports, Exports, Calls, References, Extends, Implements, Uses, Tests, DependsOn }

public class GraphEdge
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public GraphEdgeKind Kind { get; set; }
    public Guid? OwningFileId { get; set; }
    public double Weight { get; set; } = 1.0;
}

public enum SessionStatus { Active, Ended }

public class Session
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string? ClaudeSessionId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public SessionStatus Status { get; set; }
}

public enum SessionEventType { Prompt, ToolCall, FileRead, FileEdit, Command, Error }

public class SessionEvent
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public SessionEventType Type { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int? TokenEstimate { get; set; }
}

public class SessionSummary
{
    public Guid SessionId { get; set; }                 // PK
    public string? Completed { get; set; }
    public string? Discovered { get; set; }
    public string? Decisions { get; set; }
    public string? ChangedFilesJson { get; set; }
    public string? OpenIssues { get; set; }
    public string? NextSteps { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

public enum MemoryType { Decision, Discovery, BugFix, Feature, Refactor, Change, Exploration, Architecture, Issue }
public enum MemoryTier { Working, LongTerm }

public class Memory
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public MemoryType Type { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public double Confidence { get; set; } = 0.7;
    public MemoryTier Tier { get; set; } = MemoryTier.Working;
    public bool Pinned { get; set; }
    public int ReinforcementCount { get; set; }
    public Guid? SupersededById { get; set; }
    public Guid? CreatedFromSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastReinforcedAt { get; set; }
}

public enum MemoryRelationKind { Supersedes, RelatesTo, Contradicts }

public class MemoryRelation
{
    public Guid Id { get; set; }
    public Guid MemoryId { get; set; }
    public Guid RelatedMemoryId { get; set; }
    public MemoryRelationKind Kind { get; set; }
}

public class MemoryFile { public Guid MemoryId { get; set; } public Guid FileId { get; set; } }
public class MemorySymbol { public Guid MemoryId { get; set; } public Guid SymbolId { get; set; } }

public class MemoryEmbedding
{
    public Guid MemoryId { get; set; }                  // PK
    public required byte[] Vector { get; set; }
    public int Dim { get; set; }
}

public enum JobType { IndexFile, DeleteFile, ParseFile, ResolveRelations, UpdateGraph, EmbedChunk, SummarizeSession, ExtractMemory, DecayMemory, BackupDb }
public enum JobStatus { Pending, Running, Done, Failed }

public class Job
{
    public Guid Id { get; set; }
    public JobType Type { get; set; }
    public required string PayloadJson { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Priority { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
}

public enum ProviderCategory { Embedding, Reranker, Llm }

public class Provider
{
    public Guid Id { get; set; }
    public ProviderCategory Category { get; set; }
    public required string Name { get; set; }           // "openai" | "ollama" | "local-onnx" | "anthropic"
    public string? ConfigJson { get; set; }              // never raw secrets — see Context.Claude §7
    public bool IsDefault { get; set; }
}

public class ProviderModel
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public required string ModelName { get; set; }
    public int? Dimensions { get; set; }
}

public class ClaudeIntegration
{
    public Guid ProjectId { get; set; }                 // PK
    public required string HookVersion { get; set; }
    public required string McpConfigHash { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public string? BackupPath { get; set; }
}
```

---

## 2. Context.Shared (`Context.Shared`)

```csharp
namespace Context.Shared;

public readonly record struct Result<T>(bool Success, T? Value, string? Error)
{
    public static Result<T> Ok(T value) => new(true, value, null);
    public static Result<T> Fail(string error) => new(false, default, error);
}

public static class TokenEstimator
{
    // Cheap, provider-agnostic heuristic: ~4 chars/token for code+prose mixed content.
    // Used for budget accounting everywhere — NOT for provider billing accuracy.
    public static int Estimate(string text) => Math.Max(1, text.Length / 4);
}

public enum DisclosureLevel { L0_Metadata, L1_Summary, L2_Full }
```

---

## 3. Context.Indexing (`Context.Indexing`)

```csharp
namespace Context.Indexing.Abstractions;

public interface IIgnoreMatcher
{
    bool IsIgnored(string relativePath);
}

public interface IContentHasher
{
    string HashFile(string absolutePath);
    string HashText(string text);
}

public record SymbolDraft(
    string Name, string QualifiedName, Context.Domain.Enums.SymbolKind Kind,
    int StartLine, int EndLine, string SignatureText, string BodyText, string? DocComment,
    string? ParentQualifiedName);

public record RelationDraft(
    string SourceQualifiedName, string TargetName, Context.Domain.Enums.RelationKind Kind);

public record ParsedAst(object Tree, string SourceText); // Tree is parser-specific (tree-sitter node or internal AST)

public interface ILanguageParser
{
    IReadOnlySet<string> SupportedExtensions { get; }
    ParsedAst Parse(string sourceText, ParsedAst? previousTree = null);
    IReadOnlyList<SymbolDraft> ExtractSymbols(ParsedAst ast);
    IReadOnlyList<RelationDraft> ExtractRelations(ParsedAst ast);
}

public interface IFileScanner
{
    IAsyncEnumerable<ScannedFile> ScanAsync(string rootPath, IIgnoreMatcher ignore, CancellationToken ct);
}

public record ScannedFile(string RelativePath, string AbsolutePath, long SizeBytes);
```

`Context.Indexing` ships `ILanguageParser` implementations for TypeScript/JavaScript,
C#, and Python behind a **regex/heuristic MVP parser** (`RegexHeuristicParser`) that
implements the same interface so the pipeline is fully wired end-to-end; a real
tree-sitter-backed parser is a drop-in replacement registered via DI and does not
change any downstream contract.

---

## 4. Context.Graph (`Context.Graph`)

```csharp
namespace Context.Graph.Abstractions;

public interface IGraphStore
{
    Task UpsertAsync(IEnumerable<Context.Domain.Entities.GraphNode> nodes,
                      IEnumerable<Context.Domain.Entities.GraphEdge> edges, CancellationToken ct);
    Task InvalidateByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct);
}

public interface IGraphTraversal
{
    Task<IReadOnlyList<Context.Domain.Entities.GraphNode>> NeighborsAsync(
        Guid nodeId, Context.Domain.Enums.GraphEdgeKind[]? kinds, int hops, CancellationToken ct);
    Task<IReadOnlyList<Context.Domain.Entities.GraphNode>?> ShortestPathAsync(
        Guid fromId, Guid toId, CancellationToken ct);
    Task<ImpactSet> BlastRadiusAsync(Guid symbolNodeId, int maxHops, CancellationToken ct);
}

public record ImpactNode(Context.Domain.Entities.GraphNode Node, int Hops, string Path);
public record ImpactSet(IReadOnlyList<ImpactNode> Callers, IReadOnlyList<ImpactNode> Implementers, IReadOnlyList<ImpactNode> CoveringTests);
```

---

## 5. Context.Providers (`Context.Providers`)

Since the Microsoft.Extensions.AI provider migration (see ROADMAP.md's "✅ Done" section),
embedding and LLM provider selection is expressed directly in terms of
Microsoft.Extensions.AI's own abstractions — there is no bespoke `IEmbeddingProvider`/
`ILlmProvider` interface in this project any more; `AddContextProviders` registers an
`IEmbeddingGenerator<string, Embedding<float>>` and an `IChatClient` (both
`Microsoft.Extensions.AI` types) via that library's own `AddEmbeddingGenerator`/
`AddChatClient` DI helpers. `IRerankerProvider` is the one provider-category interface
still local to this project (nothing in `Microsoft.Extensions.AI` covers reranking):

```csharp
namespace Context.Providers.Abstractions;

public interface IRerankerProvider
{
    string Name { get; }
    Task<IReadOnlyList<int>> RerankAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct);
    // returns candidate indices best-first
}
```

Built-ins, selected by `ContextProvidersOptions.EmbeddingProvider`/`RerankerProvider`/`LlmProvider`
(see `ServiceCollectionExtensions.AddContextProviders`): `NoOpEmbeddingGenerator` (deterministic
hash-based pseudo-embedding, for running the pipeline with zero external dependencies / offline
tests) and `NoOpChatClient` (deterministic templated response, no network call); real embedding/chat
providers wrap the official OpenAI SDK, OllamaSharp and Anthropic.SDK clients as
`IEmbeddingGenerator`/`IChatClient` (`OllamaEmbeddingGeneratorDecorator`/`OllamaChatClientDecorator`
add Ollama-specific error framing, `SecretRedactingEmbeddingGeneratorDecorator`/
`SecretRedactingChatClientDecorator` scan content leaving the machine for external providers —
see BACKLOG-08/SEC-4); `LexicalOverlapReranker` (default, lexical token-overlap heuristic — not
ML-based; see the commit that renamed it from `LocalOnnxReranker` and dropped the unused ONNX
scaffold) and `NoOpReranker` (identity order, fallback).

---

## 6. Context.Retrieval (`Context.Retrieval`)

```csharp
namespace Context.Retrieval.Abstractions;

public record SearchQuery(Guid ProjectId, string Text, string[]? Scope, int TokenBudget = 2000, Guid? SessionId = null);

public record SearchHit(Guid NodeId, string Kind, string Label, double Score, string? Snippet);

public interface ISearchEngine
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct);
}

public record ContextPack(
    string ArchitectureSummary,
    IReadOnlyList<SearchHit> PrimaryHits,
    IReadOnlyList<string> HistoricalNotes,
    IReadOnlyList<string> OpenIssues,
    int EstimatedTokens,
    bool Truncated,
    int MoreAvailable,
    string Signature);

public interface IContextPackBuilder
{
    Task<ContextPack> BuildAsync(SearchQuery query, CancellationToken ct);
}
```

---

## 7. Context.Memory (`Context.Memory`)

```csharp
namespace Context.Memory.Abstractions;

public interface ISessionSummarizer
{
    Task<Context.Domain.Entities.SessionSummary> SummarizeAsync(Guid sessionId, CancellationToken ct);
}

public interface IMemoryExtractor
{
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractAsync(
        Context.Domain.Entities.SessionSummary summary, CancellationToken ct);
    // Handles dedup-by-embedding-similarity and supersession internally (writes via repository).
}
```

---

## 8. Context.Claude (`Context.Claude`)

```csharp
namespace Context.Claude.Hooks;

public record SessionStartInput(string SessionId, string Source); // "startup"|"resume"|"clear"
public record UserPromptSubmitInput(string SessionId, string Prompt);
public record PostToolUseInput(string SessionId, string ToolName, string ToolInputJson, string ToolResponseJson);
public record SessionEndInput(string SessionId, string Reason);

public record HookOutput(string? AdditionalContext, bool Block = false, string? BlockReason = null);

namespace Context.Claude.Installation;

public interface IClaudeIntegrationInstaller
{
    Task InstallAsync(Guid projectId, string projectRoot, CancellationToken ct);
    // Safe-merges .claude/settings.json + .mcp.json, writes hooks/*.sh + hooks/*.ps1,
    // backs up any pre-existing files it touches under .claude/.backup/<timestamp>/.
}
```

---

## 9. Context.Infrastructure (`Context.Infrastructure`)

- `ContextDbContext : DbContext` — one `DbSet<T>` per entity in §1. SQLite, WAL mode
  (`PRAGMA journal_mode=WAL` on connection open), FK constraints on.
- `ContextDbContextFactory : IDesignTimeDbContextFactory<ContextDbContext>` for
  `dotnet ef migrations` tooling.
- Connection string: `Data Source={ProjectDataDir}/context.db` where
  `ProjectDataDir` = `<repo-root>/.context/` (created + gitignored by installer).
- Repositories live here implementing interfaces declared in `Context.Application`
  (e.g. `IProjectRepository`, `IFileRepository`, `ISymbolRepository`, `IMemoryRepository`,
  `IJobRepository`) — Application depends only on the interfaces.

---

## 10. Context.Application (`Context.Application`)

```csharp
namespace Context.Application.Jobs;

public interface IJobQueue
{
    Task<Guid> EnqueueAsync(Context.Domain.Enums.JobType type, object payload, int priority = 0, CancellationToken ct = default);
}

namespace Context.Application;

// The single facade MCP / CLI / API controllers all call — never touch repositories directly.
public interface IContextEngine
{
    Task<Context.Retrieval.Abstractions.ContextPack> SearchContextAsync(Context.Retrieval.Abstractions.SearchQuery query, CancellationToken ct);
    Task<object> ExpandAsync(Guid nodeId, Context.Shared.DisclosureLevel level, CancellationToken ct);
    Task<Context.Graph.Abstractions.ImpactSet> GraphImpactAsync(Guid symbolId, int maxHops, CancellationToken ct);
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct);
    Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct);
    Task RecordEventAsync(Guid sessionId, Context.Domain.Enums.SessionEventType type, object payload, CancellationToken ct);
    Task EndSessionAsync(Guid sessionId, CancellationToken ct); // enqueues SummarizeSession -> ExtractMemory
    Task<object> ProjectInfoAsync(Guid projectId, CancellationToken ct);
}
```

MCP tools (Context.Mcp, hosted inside `Context.Host`), CLI commands (`Context.Cli`)
and REST controllers (`Context.Host`) are thin adapters over `IContextEngine` only.

---

## 12. Dashboard Settings / System-health surface (this session's addition)

New, additive-only contracts backing the Dashboard's Settings tab and System/health tab.
Nothing here changes an existing signature.

```csharp
namespace Context.Application.Settings;

// Persisted verbatim (as JSON) into Project.ConfigJson — the whole field is this DTO's
// serialization, nothing else uses ConfigJson today. Provider fields are informational/
// override-on-next-restart only: Context.Providers wires concrete IEmbeddingProvider/
// ILlmProvider instances once at Context.Host startup via DI (see §5), there is no
// per-request/per-project provider resolution, so changing these fields does NOT hot-swap
// the running provider — the Settings UI must say so next to the provider fields.
// ReindexEnabled/ReindexIntervalMinutes DO take effect live (ReindexSchedulerService polls
// this DTO), no restart needed.
public sealed record ProjectSettingsDto(
    string EmbeddingProvider,          // "noop" | "openai" | "ollama"
    string LlmProvider,                // "noop" | "anthropic" | "ollama"
    string? OllamaBaseUrl,
    string? OllamaEmbeddingModel,
    string? OllamaChatModel,
    bool ReindexEnabled,
    int ReindexIntervalMinutes,        // ignored (treated as disabled) if <= 0
    DateTimeOffset? LastReindexScheduledAt);

public interface IProjectSettingsService
{
    // Returns saved settings from Project.ConfigJson; if a project has never saved settings,
    // returns a DTO seeded from the host's current IOptions<Context.Providers.ContextProvidersOptions>
    // (so the UI shows real effective values, not blanks) with ReindexEnabled=false, ReindexIntervalMinutes=60.
    Task<ProjectSettingsDto> GetAsync(Guid projectId, CancellationToken ct);

    // Serializes settings to Project.ConfigJson and persists via IProjectRepository.UpdateAsync.
    Task UpdateAsync(Guid projectId, ProjectSettingsDto settings, CancellationToken ct);
}

namespace Context.Application.Indexing;

// Fire-and-forget progress channel from IndexingPipeline to whatever wants to observe a
// reindex live (Context.Host wires a SignalR-backed implementation; default/fallback is a
// no-op so Context.Application has zero hard dependency on SignalR).
public sealed record ReindexProgressDto(Guid ProjectId, int FilesProcessed, int FilesTotal, bool Completed);

public interface IIndexingProgressReporter
{
    Task ReportAsync(ReindexProgressDto progress, CancellationToken ct);
}
// NullIndexingProgressReporter (Context.Application.Indexing) is the DI default.
// IndexingPipeline gains a new trailing optional constructor parameter
// `IIndexingProgressReporter? progressReporter = null` (falls back to NullIndexingProgressReporter.Instance,
// exact same nullable-fallback pattern already used for ILogger<IndexingPipeline> — see its ctor).
// ReindexProjectAsync calls ReportAsync once per file processed (FilesProcessed running count,
// FilesTotal = total files discovered for this reindex, Completed=false) and once more at the
// very end with Completed=true. This must NOT change IIndexingPipeline's existing interface
// signature — it is purely an added constructor seam on the concrete IndexingPipeline class.

namespace Context.Application.Scheduling;

// Registered via services.AddHostedService<ReindexSchedulerService>() in AddContextApplication.
// Every PollInterval (const, 60s), lists all projects (IProjectRepository.ListAsync), reads each
// project's ProjectSettingsDto (IProjectSettingsService.GetAsync); if ReindexEnabled and
// (LastReindexScheduledAt is null or DateTimeOffset.UtcNow - LastReindexScheduledAt.Value >=
// TimeSpan.FromMinutes(ReindexIntervalMinutes)), triggers a non-full reindex by resolving
// IIndexingPipeline in a fresh IServiceScope (same pattern as Context.Host's existing
// ProjectEndpoints.TriggerIndex background Task.Run — do not block the polling loop on it,
// fire with `_ = Task.Run(...)` and swallow+log exceptions), then immediately calls
// IProjectSettingsService.UpdateAsync with LastReindexScheduledAt = DateTimeOffset.UtcNow
// BEFORE the reindex finishes (prevents a slow reindex from re-triggering itself next poll).
public sealed class ReindexSchedulerService : Microsoft.Extensions.Hosting.BackgroundService { }
```

```csharp
namespace Context.Claude.Installation;

public interface IClaudeIntegrationInstaller
{
    Task InstallAsync(Guid projectId, string projectRoot, CancellationToken ct); // unchanged

    // New. Writes .claude/skills/context-memory/SKILL.md (frontmatter: name: context-memory,
    // description: one line) — a skill document teaching Claude Code to proactively call this
    // project's registered MCP tools (context_search, code_search, symbol_search, memory_search,
    // graph_impact, session_search — see §10/ContextMcpTools) for code lookup and persistent
    // memory instead of re-deriving context from scratch. Same backup-before-overwrite convention
    // as InstallAsync (via the same private BackupIfExists helper).
    Task InstallSkillAsync(Guid projectId, string projectRoot, CancellationToken ct);
}
```

REST surface added to `Context.Host` (thin wrappers, no new business logic beyond what's above):

```
GET  /api/projects/{id}/settings          -> ProjectSettingsDto   (IProjectSettingsService.GetAsync)
PUT  /api/projects/{id}/settings          <- ProjectSettingsDto   (IProjectSettingsService.UpdateAsync), returns 204
POST /api/projects/{id}/claude/install    -> { installed: true }  (looks up Project by id for RootPath,
                                              calls IClaudeIntegrationInstaller.InstallAsync then InstallSkillAsync)
GET  /api/system/status                   -> SystemStatusDto (Context.Host.Api namespace):
  record SystemStatusDto(bool DatabaseReachable, bool JobDispatcherAlive, bool McpAlive,
                          string EmbeddingProvider, string LlmProvider,
                          int JobsPending, int JobsRunning, int JobsFailed, int JobsDone,
                          double UptimeSeconds);
  // DatabaseReachable/JobDispatcherAlive/EmbeddingProvider/LlmProvider: same checks /health already
  // does today (reuse, don't duplicate the logic - extract a shared helper if /health's handler
  // doesn't already expose one). McpAlive: true (this endpoint runs in the same process the MCP
  // endpoint is mapped in - if this responds, MCP is reachable). Jobs* fields: GroupBy(j => j.Status)
  // count over the Jobs table via IDbContextFactory<ContextDbContext> (same direct-DbContext pattern
  // ContextMcpTools.SessionSearchAsync already uses for a query with no repository method).
  // UptimeSeconds: register a singleton `readonly record struct HostStartTime(DateTimeOffset Value)`
  // in Program.cs set once at startup, inject it, subtract from DateTimeOffset.UtcNow.
```

SignalR (`Context.Host.Hubs.IndexingHubNotifier`, extend — do not break the existing method):

```csharp
public sealed class IndexingHubNotifier(IHubContext<IndexingHub> hub)
{
    public Task ProjectStatusChangedAsync(Guid projectId, object statusDto, CancellationToken ct); // unchanged
    // New: broadcasts "ReindexProgress" to the same per-project group.
    public Task ReindexProgressAsync(Guid projectId, Context.Application.Indexing.ReindexProgressDto progress, CancellationToken ct) =>
        hub.Clients.Group(IndexingHub.GroupName(projectId)).SendAsync("ReindexProgress", progress, ct);
}
```
Context.Host registers a small adapter `SignalRIndexingProgressReporter : IIndexingProgressReporter` (in `Context.Host/Hubs/`)
that wraps `IndexingHubNotifier.ReindexProgressAsync`, registered AFTER `AddContextApplication` so it overrides
the `NullIndexingProgressReporter` default registration (last registration for a given interface wins with
`services.AddSingleton`, so order in `Program.cs` matters — see how `AddContextProviders` already overrides
Context.Application's own placeholder registrations for the same reason).

---

## 13. Naming & folder conventions

- One class per file, filename == type name.
- `Abstractions/` subfolder per project for interfaces/DTOs, implementation classes
  at project root or feature subfolder (`Parsers/`, `Repositories/`, `Providers/`).
- All async methods take a trailing `CancellationToken ct`.
- No static mutable state. All cross-project wiring via `Microsoft.Extensions.DependencyInjection`
  `IServiceCollection` extension methods named `Add{ProjectShortName}` (e.g. `AddContextIndexing`),
  defined in a `ServiceCollectionExtensions.cs` at the project root, called from `Context.Host/Program.cs`.
