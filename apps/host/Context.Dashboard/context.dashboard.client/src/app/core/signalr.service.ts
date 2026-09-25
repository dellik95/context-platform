import { Injectable, OnDestroy, inject } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { ProjectTokenService } from './project-token.service';
import { HookInvocationEvent, JobCountsChangedEvent, ProjectActivityEvent, ProjectStatusChangedEvent, ReindexProgressEvent } from './models';

/**
 * Wraps the /hubs/indexing SignalR hub (apps/host/Context.Host/Hubs/IndexingHub.cs). One
 * connection per app instance; components join/leave per-project groups as they mount/unmount
 * so the Dashboard reacts to a reindex finishing instead of polling for it.
 */
@Injectable({ providedIn: 'root' })
export class SignalrService implements OnDestroy {
  private readonly projectTokenService = inject(ProjectTokenService);

  private connection: signalR.HubConnection | null = null;
  private startPromise: Promise<void> | null = null;
  private readonly joinedProjects = new Set<string>();
  // The connection is shared app-wide but the token is now per-project -- there's no single
  // token to authenticate the WebSocket upgrade with. Since a browser tab is scoped to one
  // project at a time (the deep-link access model ProjectTokenService documents), the most
  // recently joined project's token is used for connect/reconnect; accessTokenFactory re-reads
  // this field each time rather than closing over a stale value.
  private activeProjectId: string | null = null;

  readonly projectStatusChanged$ = new Subject<ProjectStatusChangedEvent>();
  readonly reindexProgress$ = new Subject<ReindexProgressEvent>();
  readonly projectActivity$ = new Subject<ProjectActivityEvent>();
  readonly jobCountsChanged$ = new Subject<JobCountsChangedEvent>();
  readonly hookInvoked$ = new Subject<HookInvocationEvent>();

  private async ensureStarted(): Promise<void> {
    if (this.connection) {
      return this.startPromise ?? Promise.resolve();
    }

    this.connection = new signalR.HubConnectionBuilder()
      // Browsers can't set custom headers on a WebSocket upgrade, so accessTokenFactory is
      // SignalR's own mechanism for this: its JS client sends the resolved value as an
      // ?access_token= query-string parameter on the negotiate/connect requests instead (see
      // Context.Host.Security.BearerTokenAuth, which accepts the token from either place for
      // exactly this reason).
      .withUrl(`${environment.apiBaseUrl}/hubs/indexing`, {
        accessTokenFactory: async () =>
          (this.activeProjectId && this.projectTokenService.getToken(this.activeProjectId)) || '',
      })
      .withAutomaticReconnect()
      .build();

    this.connection.on('ProjectStatusChanged', (dto: ProjectStatusChangedEvent) =>
      this.projectStatusChanged$.next(dto));

    this.connection.on('ReindexProgress', (dto: ReindexProgressEvent) =>
      this.reindexProgress$.next(dto));

    this.connection.on('ProjectActivity', (dto: ProjectActivityEvent) =>
      this.projectActivity$.next(dto));

    // Project-scoped -- same JoinProject group as ProjectStatusChanged/ReindexProgress
    // (IndexingHubNotifier.HookInvokedAsync uses Clients.Group(GroupName(projectId))).
    this.connection.on('HookInvoked', (dto: HookInvocationEvent) =>
      this.hookInvoked$.next(dto));

    // Host-wide -- broadcast to every connected client (IndexingHubNotifier.JobCountsChangedAsync
    // uses Clients.All, not a per-project group), so no JoinProject membership is needed to
    // receive it.
    this.connection.on('JobCountsChanged', (dto: JobCountsChangedEvent) =>
      this.jobCountsChanged$.next(dto));

    // Automatic reconnect drops group membership server-side; rejoin whatever this client cared
    // about once the connection comes back.
    this.connection.onreconnected(() => {
      for (const projectId of this.joinedProjects) {
        this.connection?.invoke('JoinProject', projectId)
          .catch(err => console.warn(`Failed to rejoin project ${projectId}`, err));
      }
    });

    this.startPromise = this.connection.start();
    return this.startPromise;
  }

  async joinProject(projectId: string): Promise<void> {
    if (!projectId) {
      return;
    }
    this.activeProjectId = projectId;
    await this.ensureStarted();
    this.joinedProjects.add(projectId);
    await this.connection?.invoke('JoinProject', projectId);
  }

  async leaveProject(projectId: string): Promise<void> {
    // The hub binds the argument to a Guid, so '' (no project selected yet) fails server-side.
    if (!projectId || !this.joinedProjects.delete(projectId)) {
      return;
    }
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('LeaveProject', projectId)
        .catch(err => console.warn(`Failed to leave project ${projectId}`, err));
    }
  }

  ngOnDestroy(): void {
    void this.connection?.stop();
  }
}
