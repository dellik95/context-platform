import { Injectable, OnDestroy, inject } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthTokenService } from './auth-token.service';
import { ProjectActivityEvent, ProjectStatusChangedEvent, ReindexProgressEvent } from './models';

/**
 * Wraps the /hubs/indexing SignalR hub (apps/host/Context.Host/Hubs/IndexingHub.cs). One
 * connection per app instance; components join/leave per-project groups as they mount/unmount
 * so the Dashboard reacts to a reindex finishing instead of polling for it.
 */
@Injectable({ providedIn: 'root' })
export class SignalrService implements OnDestroy {
  private readonly authTokenService = inject(AuthTokenService);

  private connection: signalR.HubConnection | null = null;
  private startPromise: Promise<void> | null = null;
  private readonly joinedProjects = new Set<string>();

  readonly projectStatusChanged$ = new Subject<ProjectStatusChangedEvent>();
  readonly reindexProgress$ = new Subject<ReindexProgressEvent>();
  readonly projectActivity$ = new Subject<ProjectActivityEvent>();

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
        accessTokenFactory: async () => (await this.authTokenService.getToken()) ?? '',
      })
      .withAutomaticReconnect()
      .build();

    this.connection.on('ProjectStatusChanged', (dto: ProjectStatusChangedEvent) =>
      this.projectStatusChanged$.next(dto));

    this.connection.on('ReindexProgress', (dto: ReindexProgressEvent) =>
      this.reindexProgress$.next(dto));

    this.connection.on('ProjectActivity', (dto: ProjectActivityEvent) =>
      this.projectActivity$.next(dto));

    // Automatic reconnect drops group membership server-side; rejoin whatever this client cared
    // about once the connection comes back.
    this.connection.onreconnected(() => {
      for (const projectId of this.joinedProjects) {
        void this.connection?.invoke('JoinProject', projectId);
      }
    });

    this.startPromise = this.connection.start();
    return this.startPromise;
  }

  async joinProject(projectId: string): Promise<void> {
    await this.ensureStarted();
    this.joinedProjects.add(projectId);
    await this.connection?.invoke('JoinProject', projectId);
  }

  async leaveProject(projectId: string): Promise<void> {
    this.joinedProjects.delete(projectId);
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('LeaveProject', projectId);
    }
  }

  ngOnDestroy(): void {
    void this.connection?.stop();
  }
}
