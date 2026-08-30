import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  BrowseDirectoryResponse,
  ClaudeInstallResponse,
  ContextPack,
  CreateProjectRequest,
  HealthResponse,
  ImpactSet,
  IndexTriggerResponse,
  Memory,
  ProjectDto,
  ProjectSettingsDto,
  ProjectStatusDto,
  SessionDetailDto,
  SessionDto,
  SymbolSummaryDto,
  SystemStatusDto,
} from './models';

/**
 * Thin typed wrapper over Context.Host's REST surface (apps/host/Context.Host/Api/*.cs).
 * One method per endpoint, no business logic — feature components own presentation/state.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  health(): Observable<HealthResponse> {
    return this.http.get<HealthResponse>(`${this.base}/health`);
  }

  listProjects(): Observable<ProjectDto[]> {
    return this.http.get<ProjectDto[]>(`${this.base}/api/projects`);
  }

  createProject(request: CreateProjectRequest): Observable<ProjectDto> {
    return this.http.post<ProjectDto>(`${this.base}/api/projects`, request);
  }

  getProjectStatus(id: string): Observable<ProjectStatusDto> {
    return this.http.get<ProjectStatusDto>(`${this.base}/api/projects/${id}`);
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

  searchMemories(projectId: string, query: string, includeSuperseded = false): Observable<Memory[]> {
    const params = new URLSearchParams({ projectId, q: query, includeSuperseded: String(includeSuperseded) });
    return this.http.get<Memory[]>(`${this.base}/api/memories?${params.toString()}`);
  }

  graphImpact(symbolId: string, maxHops = 3): Observable<ImpactSet> {
    const params = new URLSearchParams({ symbolId, maxHops: String(maxHops) });
    return this.http.get<ImpactSet>(`${this.base}/api/graph/impact?${params.toString()}`);
  }

  searchSymbols(projectId: string, query: string, limit = 20): Observable<SymbolSummaryDto[]> {
    const params = new URLSearchParams({ projectId, q: query, limit: String(limit) });
    return this.http.get<SymbolSummaryDto[]>(`${this.base}/api/symbols?${params.toString()}`);
  }

  listSessions(projectId: string, limit = 50): Observable<SessionDto[]> {
    const params = new URLSearchParams({ projectId, limit: String(limit) });
    return this.http.get<SessionDto[]>(`${this.base}/api/sessions?${params.toString()}`);
  }

  getSession(id: string): Observable<SessionDetailDto> {
    return this.http.get<SessionDetailDto>(`${this.base}/api/sessions/${id}`);
  }

  getSettings(projectId: string): Observable<ProjectSettingsDto> {
    return this.http.get<ProjectSettingsDto>(`${this.base}/api/projects/${projectId}/settings`);
  }

  updateSettings(projectId: string, settings: ProjectSettingsDto): Observable<void> {
    return this.http.put<void>(`${this.base}/api/projects/${projectId}/settings`, settings);
  }

  installClaude(projectId: string): Observable<ClaudeInstallResponse> {
    return this.http.post<ClaudeInstallResponse>(`${this.base}/api/projects/${projectId}/claude/install`, {});
  }

  getSystemStatus(): Observable<SystemStatusDto> {
    return this.http.get<SystemStatusDto>(`${this.base}/api/system/status`);
  }

  browseDirectory(path?: string): Observable<BrowseDirectoryResponse> {
    const params = path ? `?path=${encodeURIComponent(path)}` : '';
    return this.http.get<BrowseDirectoryResponse>(`${this.base}/api/fs/browse${params}`);
  }
}
