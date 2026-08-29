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
