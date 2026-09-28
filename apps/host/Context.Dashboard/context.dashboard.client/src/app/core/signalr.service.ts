import { Injectable, OnDestroy, inject } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { ProjectTokenService } from './project-token.service';
import { HookInvocationEvent, JobCountsChangedEvent, ProjectActivityEvent, ProjectStatusChangedEvent, ReindexProgressEvent } from './models';

/**
 * Wraps the /hubs/indexing SignalR hub (Context.Dashboard.Server/Hubs/IndexingHub.cs). Components
 * join/leave per-project groups as they mount/unmount so the Dashboard reacts to a reindex
 * finishing instead of polling for it.
 */
@Injectable({ providedIn: 'root' })
export class SignalrService implements OnDestroy {
  private readonly projectTokenService = inject(ProjectTokenService);

  private connection: signalR.HubConnection | null = null;
  private startPromise: Promise<void> | null = null;
  private readonly joinedProjects = new Set<string>();
  // The hub only lets a connection join the project its token belongs to, and tokens are
  // per-project, so each connection is bound to one project and replaced on a project switch.
  private connectionProjectId: string | null = null;

  readonly projectStatusChanged$ = new Subject<ProjectStatusChangedEvent>();
  readonly reindexProgress$ = new Subject<ReindexProgressEvent>();
  readonly projectActivity$ = new Subject<ProjectActivityEvent>();
  readonly jobCountsChanged$ = new Subject<JobCountsChangedEvent>();
  readonly hookInvoked$ = new Subject<HookInvocationEvent>();

  private connect(projectId: string): signalR.HubConnection {
    const previous = this.connection;
    this.joinedProjects.clear();
    void previous?.stop();

    const connection = new signalR.HubConnectionBuilder()
      // Browsers can't set headers on a WebSocket upgrade; the JS client sends this as
      // ?access_token=, which BearerTokenAuth accepts for the hub path.
      .withUrl(`${environment.apiBaseUrl}/hubs/indexing`, {
        accessTokenFactory: async () => this.projectTokenService.getToken(projectId) || '',
      })
      .withAutomaticReconnect()
      .build();

    connection.on('ProjectStatusChanged', (dto: ProjectStatusChangedEvent) =>
      this.projectStatusChanged$.next(dto));
    connection.on('ReindexProgress', (dto: ReindexProgressEvent) =>
      this.reindexProgress$.next(dto));
    connection.on('ProjectActivity', (dto: ProjectActivityEvent) =>
      this.projectActivity$.next(dto));
    connection.on('HookInvoked', (dto: HookInvocationEvent) =>
      this.hookInvoked$.next(dto));
    connection.on('JobCountsChanged', (dto: JobCountsChangedEvent) =>
      this.jobCountsChanged$.next(dto));

    // Automatic reconnect drops group membership server-side.
    connection.onreconnected(() => {
      for (const joined of this.joinedProjects) {
        connection.invoke('JoinProject', joined)
          .catch(err => console.warn(`Failed to rejoin project ${joined}`, err));
      }
    });

    this.connection = connection;
    this.connectionProjectId = projectId;
    this.startPromise = connection.start().catch(err => {
      // Let the next join retry instead of awaiting this rejected start forever.
      if (this.connection === connection) {
        this.connection = null;
        this.connectionProjectId = null;
        this.startPromise = null;
      }
      throw err;
    });
    return connection;
  }

  async joinProject(projectId: string): Promise<void> {
    if (!projectId) {
      return;
    }
    const connection = this.connection && this.connectionProjectId === projectId
      ? this.connection
      : this.connect(projectId);
    await this.startPromise;
    if (this.connection !== connection) {
      return;
    }
    this.joinedProjects.add(projectId);
    await connection.invoke('JoinProject', projectId);
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
