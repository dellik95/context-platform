import { HttpClient, HttpResourceRef, httpResource } from '@angular/common/http';
import { Injectable, Signal, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  BrowseDirectoryResponse,
  CleanupJobsResponse,
  CleanupReconsolidationReportsResponse,
  ClaudeInstallResponse,
  ConsolidationReportStatus,
  ContextPack,
  CreateProjectRequest,
  ForceRunningJobsResponse,
  GraphNeighborhoodDto,
  HealthResponse,
  HookStatusDto,
  ImpactSet,
  IndexTriggerResponse,
  JobDto,
  JobStatus,
  JobType,
  JobTypeCountDto,
  LogTailDto,
  Memory,
  MemoryDetail,
  ProjectDto,
  ProjectSettingsDto,
  ProjectStatusDto,
  ReconsolidationReportDto,
  ReconsolidationReportSummaryDto,
  ReconsolidationTriggerResponse,
  RetryAllJobsResponse,
  SessionDetailDto,
  SessionListItemDto,
  SessionSummaryChunkDto,
  SymbolSummaryDto,
  SystemStatusDto,
  UsageSummaryDto,
} from './models';

/**
 * Thin typed wrapper over Context.Dashboard.Server's REST surface (apps/host/Context.Dashboard/Context.Dashboard.Server/Api/*.cs).
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

  /** Admin-gated (see auth.interceptor.ts's isAdminGated) -- mints a fresh bearer token for a
   *  project this browser has lost access to. Only ProjectContextService.init calls this. */
  reissueToken(id: string): Observable<ProjectDto> {
    return this.http.post<ProjectDto>(`${this.base}/api/projects/${id}/token`, {});
  }

  /** Backend responds 404 when nothing is currently running for this project. */
  cancelIndex(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/api/projects/${id}/index/cancel`, {});
  }

  /** Permanently removes the project (and its cascaded data) from Context Platform. */
  deleteProject(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/api/projects/${id}`);
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

  memoriesResource(
    params: Signal<{ projectId: string; query: string; includeSuperseded: boolean } | undefined>,
  ): HttpResourceRef<Memory[]> {
    return httpResource<Memory[]>(() => {
      const p = params();
      if (!p) return undefined;
      const requestParams: Record<string, string> = { projectId: p.projectId, q: p.query, includeSuperseded: String(p.includeSuperseded) };
      return { url: `${this.base}/api/memories`, params: requestParams };
    }, { defaultValue: [] });
  }

  memoryDetail(id: string): Observable<MemoryDetail> {
    return this.http.get<MemoryDetail>(`${this.base}/api/memories/${id}`);
  }

  /** Queues the delete (202); the memory disappears once the job runs, announced by a MemoryChanged push. */
  deleteMemory(id: string): Observable<{ jobId: string; status: string }> {
    return this.http.delete<{ jobId: string; status: string }>(`${this.base}/api/memories/${id}`);
  }

  /** Queues a reconsolidation job; a dry run only stores a Pending report to review. */
  triggerReconsolidation(projectId: string, dryRun: boolean): Observable<ReconsolidationTriggerResponse> {
    return this.http.post<ReconsolidationTriggerResponse>(
      `${this.base}/api/memories/reconsolidation?projectId=${projectId}&dryRun=${dryRun}`, {});
  }

  reconsolidationReportsResource(projectId: Signal<string>): HttpResourceRef<ReconsolidationReportSummaryDto[]> {
    return httpResource<ReconsolidationReportSummaryDto[]>(() => {
      const id = projectId();
      return id ? { url: `${this.base}/api/memories/reconsolidation/reports`, params: { projectId: id } } : undefined;
    }, { defaultValue: [] });
  }

  reconsolidationReportResource(reportId: Signal<string | null>): HttpResourceRef<ReconsolidationReportDto | undefined> {
    return httpResource<ReconsolidationReportDto>(() => {
      const id = reportId();
      return id ? `${this.base}/api/memories/reconsolidation/reports/${id}` : undefined;
    });
  }

  /** Backend responds 409 when the report is no longer Pending. */
  applyReconsolidation(reportId: string, excludeGroups: number[]): Observable<ReconsolidationTriggerResponse> {
    return this.http.post<ReconsolidationTriggerResponse>(
      `${this.base}/api/memories/reconsolidation/reports/${reportId}/apply`, { excludeGroups });
  }

  discardReconsolidation(reportId: string): Observable<void> {
    return this.http.post<void>(`${this.base}/api/memories/reconsolidation/reports/${reportId}/discard`, {});
  }

  /** Backend responds 404 when the report is gone and 409 while it is Applying. */
  deleteReconsolidationReport(reportId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/api/memories/reconsolidation/reports/${reportId}`);
  }

  /** Without a status removes every Applied, Discarded and Failed report; Pending ones are kept. */
  cleanupReconsolidationReports(status?: ConsolidationReportStatus): Observable<CleanupReconsolidationReportsResponse> {
    const params: Record<string, string> = {};
    if (status) {
      params['status'] = status;
    }
    return this.http.delete<CleanupReconsolidationReportsResponse>(
      `${this.base}/api/memories/reconsolidation/reports`, { params });
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

  sessionSummaryChunksResource(sessionId: Signal<string | null>): HttpResourceRef<SessionSummaryChunkDto[]> {
    return httpResource<SessionSummaryChunkDto[]>(() => {
      const id = sessionId();
      return id ? `${this.base}/api/sessions/${id}/summary-chunks` : undefined;
    }, { defaultValue: [] });
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

  /** Job counts arrive live over SignalR (see SignalrService.jobCountsChanged$); `reload()` it on a
   *  long interval as a reconciliation fallback for the rest of this DTO (db/provider/mcp liveness,
   *  uptime baseline), which rarely changes outside a restart. */
  systemStatusResource(): HttpResourceRef<SystemStatusDto | undefined> {
    return httpResource<SystemStatusDto>(() => `${this.base}/api/system/status`);
  }

  browseDirectoryResource(path: Signal<string | undefined>): HttpResourceRef<BrowseDirectoryResponse | undefined> {
    return httpResource<BrowseDirectoryResponse>(() => {
      const p = path();
      const params: Record<string, string> = p ? { path: p } : {};
      return { url: `${this.base}/api/fs/browse`, params };
    });
  }

  /** Pass `enabled` to hold the fetch until it turns true. */
  jobsResource(projectId: Signal<string>, status: JobStatus, enabled?: Signal<boolean>): HttpResourceRef<JobDto[]> {
    return httpResource<JobDto[]>(() => {
      const id = projectId();
      return id && (enabled?.() ?? true) ? { url: `${this.base}/api/jobs`, params: { status, projectId: id } } : undefined;
    }, { defaultValue: [] });
  }

  jobCountsByTypeResource(projectId: Signal<string>, status: JobStatus, enabled?: Signal<boolean>): HttpResourceRef<JobTypeCountDto[]> {
    return httpResource<JobTypeCountDto[]>(() => {
      const id = projectId();
      return id && (enabled?.() ?? true) ? { url: `${this.base}/api/jobs/counts-by-type`, params: { status, projectId: id } } : undefined;
    }, { defaultValue: [] });
  }

  retryJob(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/api/jobs/${id}/retry`, {});
  }

  retryAllFailedJobs(projectId: string): Observable<RetryAllJobsResponse> {
    return this.http.post<RetryAllJobsResponse>(`${this.base}/api/jobs/retry-all?projectId=${projectId}`, {});
  }

  forceJob(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/api/jobs/${id}/force`, {});
  }

  forceRunningJobs(projectId: string): Observable<ForceRunningJobsResponse> {
    return this.http.post<ForceRunningJobsResponse>(`${this.base}/api/jobs/force-running?projectId=${projectId}`, {});
  }

  cleanupJobs(projectId: string, status: 'Done' | 'Pending', type?: JobType): Observable<CleanupJobsResponse> {
    const params: Record<string, string> = { projectId, status };
    if (type) {
      params['type'] = type;
    }
    return this.http.post<CleanupJobsResponse>(`${this.base}/api/jobs/cleanup`, {}, { params });
  }

  hooksStatusResource(projectId: Signal<string>): HttpResourceRef<HookStatusDto | undefined> {
    return httpResource<HookStatusDto>(() => {
      const id = projectId();
      return id ? { url: `${this.base}/api/hooks/status`, params: { projectId: id } } : undefined;
    });
  }

  logTailResource(): HttpResourceRef<LogTailDto | undefined> {
    return httpResource<LogTailDto>(() => `${this.base}/api/logs/tail`);
  }

  /** `days` is a Signal (not a plain default param) so the Usage tab's 7/30/90 range toggle
   *  re-triggers this fetch the same way projectId switching does. */
  usageSummaryResource(projectId: Signal<string>, days: Signal<number>): HttpResourceRef<UsageSummaryDto | undefined> {
    return httpResource<UsageSummaryDto>(() => {
      const id = projectId();
      return id ? { url: `${this.base}/api/usage`, params: { projectId: id, days: days() } } : undefined;
    });
  }
}
