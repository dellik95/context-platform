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

export interface ProjectDto {
  id: string;
  name: string;
  rootPath: string;
  vcsRemote: string | null;
  createdAt: string; // ISO-8601
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

export interface SessionDetailDto {
  session: SessionDto;
  completed: string | null;
  discovered: string | null;
  decisions: string | null;
  changedFilesJson: string | null;
  openIssues: string | null;
  nextSteps: string | null;
  eventCount: number;
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

// Mirrors src/Context.Application/Indexing/ReindexProgressDto.cs, pushed over the
// /hubs/indexing SignalR hub's "ReindexProgress" event.
export interface ReindexProgressEvent {
  projectId: string;
  filesProcessed: number;
  filesTotal: number;
  completed: boolean;
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
