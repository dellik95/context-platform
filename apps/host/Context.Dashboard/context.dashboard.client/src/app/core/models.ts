// Mirrors the real backend DTOs/entities exactly — see:
//   apps/host/Context.Host/Api/*.cs (REST DTOs)
//   src/Context.Domain/Entities/*.cs, src/Context.Domain/Enums/*.cs (raw entities returned as-is
//     by /api/memories and /api/graph/impact)
// Enums serialize as their C# member name (Program.cs registers JsonStringEnumConverter) — keep
// these string literal unions in sync with the real enum member lists if either side changes.

export type MemoryType =
  | 'Decision' | 'Discovery' | 'BugFix' | 'Feature' | 'Refactor'
  | 'Change' | 'Exploration' | 'Architecture' | 'Issue';

export type MemoryTier = 'Working' | 'LongTerm';

export type GraphNodeKind =
  | 'Project' | 'Directory' | 'File' | 'Module' | 'Class' | 'Interface'
  | 'Function' | 'Method' | 'Type' | 'Variable' | 'Package' | 'Test' | 'UnresolvedExternal';

export type GraphEdgeKind =
  | 'Contains' | 'Imports' | 'Exports' | 'Calls' | 'References'
  | 'Extends' | 'Implements' | 'Uses' | 'Tests' | 'DependsOn';

export type JobType =
  | 'IndexFile' | 'ResolveRelations' | 'UpdateGraph'
  | 'EmbedChunk' | 'SummarizeSession' | 'ExtractMemory' | 'DecayMemory' | 'BackupDb'
  | 'RecreateEmbeddings' | 'CompactMemory';

export type JobStatus = 'Pending' | 'Running' | 'Done' | 'Failed';

export interface ProjectDto {
  id: string;
  name: string;
  rootPath: string;
  vcsRemote: string | null;
  createdAt: string; // ISO-8601
  // Non-null only in the CreateAsync/ReissueTokenAsync responses -- the raw bearer token can
  // never be recovered once minted (only its hash is persisted), so those are the only two
  // moments it's ever returned.
  token: string | null;
}

export interface ProjectStatusDto {
  project: ProjectDto;
  filesIndexed: number;
  symbolsIndexed: number;
  memoriesCount: number;
  lastIndexedAt: string | null;
}

export interface CreateProjectRequest {
  name: string;
  rootPath: string;
  vcsRemote?: string;
}

export interface DirectoryEntryDto {
  name: string;
  fullPath: string;
}

export interface BrowseDirectoryResponse {
  path: string;
  parentPath: string | null;
  directories: DirectoryEntryDto[];
}

export interface IndexTriggerResponse {
  projectId: string;
  full: boolean;
  status: string;
}

export interface HealthResponse {
  healthy: boolean;
  databaseReachable: boolean;
  jobDispatcherAlive: boolean;
  embeddingProvider: string;
  rerankerProvider: string;
  llmProvider: string;
}

export interface SearchHit {
  nodeId: string;
  kind: string;
  label: string;
  score: number;
  snippet: string | null;
}

export interface ContextPack {
  architectureSummary: string;
  primaryHits: SearchHit[];
  historicalNotes: string[];
  openIssues: string[];
  estimatedTokens: number;
  truncated: boolean;
  moreAvailable: number;
  signature: string;
}

// Raw Context.Domain.Entities.Memory, returned as-is by GET /api/memories.
export interface Memory {
  id: string;
  projectId: string;
  type: MemoryType;
  title: string;
  body: string;
  factsJson: string | null;
  // conceptsJson: fixed taxonomy tags (how-it-works, why-it-exists, what-changed,
  // problem-solution, gotcha, pattern, trade-off). filesReadJson/filesModifiedJson: the specific
  // files THIS memory is about (not every file touched in its session).
  conceptsJson: string | null;
  filesReadJson: string | null;
  filesModifiedJson: string | null;
  confidence: number;
  tier: MemoryTier;
  pinned: boolean;
  reinforcementCount: number;
  supersededById: string | null;
  createdFromSessionId: string | null;
  createdAt: string;
  lastReinforcedAt: string;
}

// Raw Context.Domain.Entities.GraphNode.
export interface GraphNode {
  id: string;
  projectId: string;
  kind: GraphNodeKind;
  refId: string | null;
  label: string;
  owningFileId: string | null;
}

export interface ImpactNode {
  node: GraphNode;
  hops: number;
  path: string;
}

export interface ImpactSet {
  callers: ImpactNode[];
  implementers: ImpactNode[];
  coveringTests: ImpactNode[];
}

// Raw Context.Graph.Abstractions.GraphEdgeDto, part of GraphNeighborhoodDto below.
export interface GraphEdgeDto {
  sourceId: string;
  targetId: string;
  kind: GraphEdgeKind;
}

// Raw Context.Graph.Abstractions.GraphNeighborhood, returned as-is by GET /api/graph/neighborhood
// -- the origin node plus every node/edge within `hops` hops of it, for the Graph tab's node/edge
// visualization (ImpactSet above is flat caller/implementer/test lists with no edges to draw).
export interface GraphNeighborhoodDto {
  nodes: GraphNode[];
  edges: GraphEdgeDto[];
}

export interface SymbolSummaryDto {
  id: string;
  name: string;
  qualifiedName: string;
  kind: string;
  fileId: string;
  startLine: number;
  endLine: number;
}

export interface SessionDto {
  id: string;
  claudeSessionId: string | null;
  startedAt: string;
  endedAt: string | null;
  status: string; // "Active" | "Ended"
}

// GET /api/sessions list rows -- title is the session summary's title, null until a summary
// exists (e.g. an Active session that hasn't been summarized yet).
export interface SessionListItemDto {
  session: SessionDto;
  title: string | null;
}

export interface SessionDetailDto {
  session: SessionDto;
  title: string | null;
  completed: string | null;
  discovered: string | null;
  decisions: string | null;
  changedFilesJson: string | null;
  openIssues: string | null;
  nextSteps: string | null;
  eventCount: number;
}

// GET /api/sessions/{id}/summary-chunks rows -- mirrors SessionSummaryChunkDto
// (apps/host/Context.Host/Api/SessionEndpoints.cs), one per append-only summary chunk, ordered by
// sequenceNumber ascending.
export interface SessionSummaryChunkDto {
  id: string;
  sessionId: string;
  sequenceNumber: number;
  fromEventAt: string;
  toEventAt: string;
  eventCount: number;
  title: string | null;
  completed: string | null;
  discovered: string | null;
  decisions: string | null;
  filesReadJson: string | null;
  changedFilesJson: string | null;
  openIssues: string | null;
  nextSteps: string | null;
  generatedAt: string;
}

/** Pushed over the /hubs/indexing SignalR hub's "ProjectStatusChanged" event. */
export type ProjectStatusChangedEvent = ProjectStatusDto;

// Mirrors apps/host/Context.Host/Api/SystemEndpoints.cs's SystemStatusDto record.
export interface SystemStatusDto {
  databaseReachable: boolean;
  jobDispatcherAlive: boolean;
  mcpAlive: boolean;
  embeddingProvider: string;
  llmProvider: string;
  jobsPending: number;
  jobsRunning: number;
  jobsFailed: number;
  jobsDone: number;
  uptimeSeconds: number;
}

// Mirrors apps/host/Context.Host/Api/JobsEndpoints.cs's JobDto record. Deliberately omits
// PayloadJson (can be arbitrarily large, not needed to see "what failed and why").
export interface JobDto {
  id: string;
  type: JobType;
  projectId: string | null;
  status: JobStatus;
  attempts: number;
  lastError: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  nextRetryAt: string | null;
}

// Response of POST /api/jobs/retry-all.
export interface RetryAllJobsResponse {
  retried: number;
  total: number;
}

// Response of POST /api/jobs/cleanup.
export interface CleanupJobsResponse {
  removed: number;
}

// Mirrors apps/host/Context.Host/Api/JobsEndpoints.cs's LogTailDto record.
export interface LogTailDto {
  lines: string[];
  filePath: string | null;
}

// Mirrors apps/host/Context.Host/Api/UsageEndpoints.cs's UsagePurposeDto record. `tokens` is real
// LLM-call spend; `estimatedSavedTokens` is the counterfactual MCP search-tool savings estimate
// (max(0, fullFileTokens - returnedTokens), see UsageRecord.SavedTokens) -- the two are never the
// same kind of number and must not be added together as if they were.
export interface UsagePurposeDto {
  purpose: string;
  tokens: number;
  calls: number;
  estimatedSavedTokens: number;
}

// Mirrors apps/host/Context.Host/Api/UsageEndpoints.cs's UsageDayDto record. day is an ISO-8601
// date (yyyy-MM-dd, no time component) -- .NET's DateOnly serializes this way by default.
// `estimatedSavedTokens` is a counterfactual estimate, not measured spend -- see UsagePurposeDto.
export interface UsageDayDto {
  day: string;
  tokens: number;
  calls: number;
  estimatedSavedTokens: number;
}

// Mirrors apps/host/Context.Host/Api/UsageEndpoints.cs's UsageSummaryDto record, returned by
// GET /api/usage?projectId={id}&days={n=30}. `totalTokens`/`totalCalls` are real spend;
// `totalEstimatedSavedTokens` is the estimate described on UsagePurposeDto; `estimatedNetTokens`
// is simply `totalEstimatedSavedTokens - totalTokens`, a convenience "profit" figure that mixes a
// real number with an estimate.
export interface UsageSummaryDto {
  totalTokens: number;
  totalCalls: number;
  totalEstimatedSavedTokens: number;
  estimatedNetTokens: number;
  byPurpose: UsagePurposeDto[];
  byDay: UsageDayDto[];
}

// Mirrors src/Context.Application/Indexing/ReindexProgressDto.cs, pushed over the
// /hubs/indexing SignalR hub's "ReindexProgress" event. `cancelled` / `failed` are only ever true
// on the synthetic final event ReindexRunner sends when a reindex is stopped via
// POST /api/projects/{id}/index/cancel or throws (completed is also true on that event -- see the
// DTO's own remarks).
export interface ReindexProgressEvent {
  projectId: string;
  filesProcessed: number;
  filesTotal: number;
  completed: boolean;
  filesFailed: number;
  cancelled: boolean;
  failed?: boolean;
}

// Mirrors Context.Application.Settings.ProjectSettingsDto exactly (see CONTRACTS.md §12 and
// apps/host/Context.Host/Api/SettingsEndpoints.cs). Provider fields are informational/
// override-on-next-restart only -- changing them does NOT hot-swap the running provider.
// ReindexEnabled/ReindexIntervalMinutes DO take effect live.
export interface ProjectSettingsDto {
  embeddingProvider: string; // "noop" | "openai" | "ollama"
  llmProvider: string; // "noop" | "anthropic" | "ollama"
  ollamaBaseUrl: string | null;
  ollamaEmbeddingModel: string | null;
  ollamaChatModel: string | null;
  reindexEnabled: boolean;
  reindexIntervalMinutes: number; // ignored (treated as disabled) if <= 0
  lastReindexScheduledAt: string | null; // ISO-8601
  sessionSummaryIntervalMinutes: number; // <= 0 means "only summarize at session end"
  minEventsForSessionSummary: number; // interval is a max wait -- also needs this many new events
  maxMemoryNotesPerSection: number; // max ranked memories shown per retrieval context section
  minEventsForMemoryExtraction: number; // raw events batched per memory-extraction LLM call (remainder flushed at session end)
  explicitRememberEnabled: boolean; // gates the memory_remember/memory_forget MCP tools
  staleSessionTimeoutMinutes: number; // <= 0 disables auto-ending idle Active sessions
  deleteSessionEventsAfterSummary: boolean; // opt-in cleanup of raw events once folded into a summary chunk
  hookInvocationRetentionDays: number; // <= 0 disables cleanup of HookInvocations rows
  memoryCompactionIntervalMinutes: number; // <= 0 disables periodic near-duplicate memory compaction
  lastMemoryCompactionScheduledAt: string | null; // ISO-8601
  memoryCompactionSimilarityThreshold: number; // (0, 1] -- cosine similarity to cluster memories for compaction
  memoryConsolidationSimilarityThreshold: number; // (0, 1] -- above this, reinforce/supersede instead of inserting new
  memoryConsolidationLowSimilarityThreshold: number; // (0, 1], <= memoryConsolidationSimilarityThreshold
  memoryConsolidationPromotionReinforcementThreshold: number; // >= 1 -- reinforcements before promotion to LongTerm tier
  memoryConsolidationTopKForDedup: number; // >= 1 -- nearest existing memories considered per new candidate
}

// Response of POST /api/projects/{id}/claude/install.
export interface ClaudeInstallResponse {
  installed: boolean;
}

export type ProjectActivityKind = 'SessionSummaryUpdated' | 'MemoryCreated';

// Pushed over the /hubs/indexing SignalR hub's "ProjectActivity" event (Context.Host's
// IndexingHub.ProjectActivityAsync) whenever a session gets a new summary or a memory is
// extracted, so Sessions/Memories panels can refresh live instead of only loading once.
export interface ProjectActivityEvent {
  projectId: string;
  kind: ProjectActivityKind;
}

// Pushed over the /hubs/indexing SignalR hub's "JobCountsChanged" event (Context.Host's
// IndexingHub.JobCountsChangedAsync), broadcast to every connected client -- not scoped to a
// project group, since the counts themselves are host-wide (same as SystemStatusDto's own
// jobsXxx fields). Mirrors Context.Application.Notifications.JobCounts.
export interface JobCountsChangedEvent {
  pending: number;
  running: number;
  failed: number;
  done: number;
}

// Mirrors src/Context.Domain/Enums/HookType.cs.
export type HookType = 'SessionStart' | 'UserPromptSubmit' | 'PostToolUse' | 'SessionEnd';

// Mirrors Context.Dashboard.Server.Api.HooksStatusEndpoints's HookInvocationDto record.
export interface HookInvocationDto {
  id: string;
  type: HookType;
  payloadJson: string;
  receivedAt: string; // ISO-8601
}

// Mirrors Context.Dashboard.Server.Api.HooksStatusEndpoints's HookStatusDto record --
// GET /api/hooks/status response.
export interface HookStatusDto {
  count: number;
  recent: HookInvocationDto[];
}

// Pushed over the /hubs/indexing SignalR hub's "HookInvoked" event (IndexingHub.HookInvokedAsync),
// project-scoped (unlike JobCountsChangedEvent). Mirrors Context.Application.Notifications.HookInvocationEvent.
export interface HookInvocationEvent {
  type: HookType;
  payloadJson: string;
  receivedAt: string; // ISO-8601
  totalCount: number;
}
