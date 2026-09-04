import { HttpClient, HttpResourceRef, httpResource } from '@angular/common/http';
import { Injectable, Signal, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  BrowseDirectoryResponse,
  ClaudeInstallResponse,
  ContextPack,
  CreateProjectRequest,
  GraphNeighborhoodDto,
  HealthResponse,
  ImpactSet,
  IndexTriggerResponse,
  JobDto,
  JobStatus,
  LogTailDto,
  Memory,
  ProjectDto,
  ProjectSettingsDto,
  ProjectStatusDto,
  SessionDetailDto,
  SessionListItemDto,
  SymbolSummaryDto,
  SystemStatusDto,
} from './models';

/**
 * Thin typed wrapper over Context.Host's REST surface (apps/host/Context.Host/Api/*.cs).
 * GET reads that a component keys off reactive state (a project id, a committed query, a poll
 * tick) go through `httpResource` so the fetch re-runs itself when that state changes. Mutations
 * (POST/PUT/DELETE) and one-shot/imperative reads stay as Observables — feature components own
 * presentation/state either way.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  health(): Observable<HealthResponse> {
    return this.http.get<HealthResponse>(`${this.base}/health`);
  }

  projectsResource(): HttpResourceRef<ProjectDto[]> {
    return httpResource<ProjectDto[]>(() => `${this.base}/api/projects`, { defaultValue: [] });
  }

  createProject(request: CreateProjectRequest): Observable<ProjectDto> {
    return this.http.post<ProjectDto>(`${this.base}/api/projects`, request);
  }

  projectStatusResource(projectId: Signal<string>): HttpResourceRef<ProjectStatusDto | undefined> {
    return httpResource<ProjectStatusDto>(() => (projectId() ? `${this.base}/api/projects/${projectId()}` : undefined));
  }

  triggerIndex(id: string, full: boolean): Observable<IndexTriggerResponse> {
    return this.http.post<IndexTriggerResponse>(`${this.base}/api/projects/${id}/index?full=${full}`, {});
  }

  search(projectId: string, query: string, budget?: number, sessionId?: string): Observable<ContextPack> {
    const params = new URLSearchParams({ projectId, q: query });
    if (budget) params.set('budget', String(budget));
    if (sessionId) params.set('sessionId', sessionId);
    return this.http.get<ContextPack>(`${this.base}/api/search?${params.toString()}`);
  }

  /** Params return `undefined` to keep the resource idle (no committed query yet). */
  searchResource(params: Signal<{ projectId: string; query: string; budget?: number } | undefined>): HttpResourceRef<ContextPack | undefined> {
    return httpResource<ContextPack>(() => {
      const p = params();
      if (!p) return undefined;
      const requestParams: Record<string, string> = { projectId: p.projectId, q: p.query };
      if (p.budget) requestParams['budget'] = String(p.budget);
      return { url: `${this.base}/api/search`, params: requestParams };
    });
  }

  searchMemories(projectId: string, query: string, includeSuperseded = false): Observable<Memory[]> {
    const params = new URLSearchParams({ projectId, q: query, includeSuperseded: String(includeSuperseded) });
    return this.http.get<Memory[]>(`${this.base}/api/memories?${params.toString()}`);
  }

  memoriesResource(params: Signal<{ projectId: string; query: string; includeSuperseded: boolean } | undefined>): HttpResourceRef<Memory[]> {
    return httpResource<Memory[]>(() => {
      const p = params();
      if (!p) return undefined;
      return { url: `${this.base}/api/memories`, params: { projectId: p.projectId, q: p.query, includeSuperseded: String(p.includeSuperseded) } };
    }, { defaultValue: [] });
  }

  deleteMemory(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/api/memories/${id}`);
  }

  graphImpact(symbolId: string, maxHops = 3): Observable<ImpactSet> {
    const params = new URLSearchParams({ symbolId, maxHops: String(maxHops) });
    return this.http.get<ImpactSet>(`${this.base}/api/graph/impact?${params.toString()}`);
  }

  getGraphNeighborhood(nodeId: string, hops = 1, kinds?: string[]): Observable<GraphNeighborhoodDto> {
    const params = new URLSearchParams({ nodeId, hops: String(hops) });
    for (const kind of kinds ?? []) params.append('kinds', kind);
    return this.http.get<GraphNeighborhoodDto>(`${this.base}/api/graph/neighborhood?${params.toString()}`);
  }

  /** Every node/edge in the project's graph — the Obsidian-style "whole graph" view. */
  getFullGraph(projectId: string, kinds?: string[]): Observable<GraphNeighborhoodDto> {
    const params = new URLSearchParams({ projectId });
    for (const kind of kinds ?? []) params.append('kinds', kind);
    return this.http.get<GraphNeighborhoodDto>(`${this.base}/api/graph/full?${params.toString()}`);
  }

  searchSymbolsResource(params: Signal<{ projectId: string; query: string } | undefined>, limit = 20): HttpResourceRef<SymbolSummaryDto[]> {
    return httpResource<SymbolSummaryDto[]>(() => {
      const p = params();
      if (!p) return undefined;
      return { url: `${this.base}/api/symbols`, params: { projectId: p.projectId, q: p.query, limit } };
    }, { defaultValue: [] });
  }

  sessionsResource(projectId: Signal<string>): HttpResourceRef<SessionListItemDto[]> {
    return httpResource<SessionListItemDto[]>(() => {
      const id = projectId();
      return id ? { url: `${this.base}/api/sessions`, params: { projectId: id, limit: 50 } } : undefined;
    }, { defaultValue: [] });
  }

  sessionDetailResource(sessionId: Signal<string | null>): HttpResourceRef<SessionDetailDto | undefined> {
    return httpResource<SessionDetailDto>(() => {
      const id = sessionId();
      return id ? `${this.base}/api/sessions/${id}` : undefined;
    });
  }

  settingsResource(projectId: Signal<string>): HttpResourceRef<ProjectSettingsDto | undefined> {
    return httpResource<ProjectSettingsDto>(() => (projectId() ? `${this.base}/api/projects/${projectId()}/settings` : undefined));
  }

  updateSettings(projectId: string, settings: ProjectSettingsDto): Observable<void> {
    return this.http.put<void>(`${this.base}/api/projects/${projectId}/settings`, settings);
  }

  installClaude(projectId: string): Observable<ClaudeInstallResponse> {
    return this.http.post<ClaudeInstallResponse>(`${this.base}/api/projects/${projectId}/claude/install`, {});
  }

  /** `tick` is read only to retrigger the fetch — bump it (e.g. from a `setInterval`) to poll.
   *  Job counts now arrive live over SignalR (see SignalrService.jobCountsChanged$); `tick` only
   *  needs to move on a long interval as a reconciliation fallback for the rest of this DTO
   *  (db/provider/mcp liveness, uptime baseline), which rarely changes outside a restart. */
  systemStatusResource(tick: Signal<number>): HttpResourceRef<SystemStatusDto | undefined> {
    return httpResource<SystemStatusDto>(() => {
      tick();
      return `${this.base}/api/system/status`;
    });
  }

  browseDirectoryResource(path: Signal<string | undefined>): HttpResourceRef<BrowseDirectoryResponse | undefined> {
    return httpResource<BrowseDirectoryResponse>(() => {
      const p = path();
      const params: Record<string, string> = p ? { path: p } : {};
      return { url: `${this.base}/api/fs/browse`, params };
    });
  }

  failedJobsResource(): HttpResourceRef<JobDto[]> {
    return httpResource<JobDto[]>(() => {
      const params = new URLSearchParams({ status: 'Failed' satisfies JobStatus });
      return `${this.base}/api/jobs?${params.toString()}`;
    }, { defaultValue: [] });
  }

  retryJob(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/api/jobs/${id}/retry`, {});
  }

  /** `requested` gates the initial fetch (stays idle until true); call the returned resource's
   *  own `.reload()` for a subsequent manual refresh. */
  logTailResource(requested: Signal<boolean>): HttpResourceRef<LogTailDto | undefined> {
    return httpResource<LogTailDto>(() => (requested() ? `${this.base}/api/logs/tail` : undefined));
  }
}
