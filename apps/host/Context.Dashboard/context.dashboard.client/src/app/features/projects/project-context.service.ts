import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, firstValueFrom, of, tap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ProjectTokenService } from '../../core/project-token.service';
import { SignalrService } from '../../core/signalr.service';
import { ReindexProgressEvent } from '../../core/models';

/**
 * Scoped to the /projects/:id route subtree — see ProjectDetailComponent's `providers` array.
 * Every panel routed under it (dashboard/search/memories/sessions/graph) injects this instead of
 * re-reading the route param and re-fetching status independently. Auto-refreshes on the
 * IndexingHub's ProjectStatusChanged push, so a reindex triggered from the Dashboard tab updates
 * everything without a manual reload.
 */
@Injectable()
export class ProjectContextService implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly projectTokenService = inject(ProjectTokenService);
  private readonly signalr = inject(SignalrService);
  private readonly snackBar = inject(MatSnackBar);

  private readonly _projectId = signal<string>('');
  private readonly _reindexing = signal(false);
  private readonly _cancelling = signal(false);
  private readonly _reindexProgress = signal<ReindexProgressEvent | null>(null);

  readonly projectId = this._projectId.asReadonly();

  private readonly statusResource = this.api.projectStatusResource(this._projectId);
  readonly status = computed(() => (this.statusResource.hasValue() ? this.statusResource.value() : null));
  /** True while there's no project id yet (nothing to show) or the status fetch is in flight. */
  readonly loading = computed(() => !this._projectId() || this.statusResource.isLoading());
  readonly projectName = computed(() => this.status()?.project.name ?? '');
  /** True from the moment a reindex is triggered until its ReindexProgress reports completed. */
  readonly reindexing = this._reindexing.asReadonly();
  readonly cancelling = this._cancelling.asReadonly();
  readonly reindexProgress = this._reindexProgress.asReadonly();

  private readonly subscriptions = [
    this.signalr.projectStatusChanged$.subscribe((dto) => {
      if (dto.project.id === this._projectId()) {
        this.statusResource.set(dto);
      }
    }),

    this.signalr.reindexProgress$.subscribe((event) => {
      if (event.projectId !== this._projectId()) {
        return;
      }
      this._reindexProgress.set(event);
      if (event.completed) {
        this._reindexing.set(false);
        const message = event.failed ? 'Reindex failed — see host logs.' : event.cancelled ? 'Reindex cancelled.' : 'Reindex complete.';
        this.snackBar.open(message, 'Dismiss', { duration: 4000 });
      }
    }),
  ];

  /**
   * When this browser has no stored bearer token for `projectId` (never captured a deep link, or
   * lost localStorage — see ProjectTokenService's own remarks), silently mints a replacement via
   * the admin-gated `POST /api/projects/{id}/token` (see ProjectEndpoints.ReissueTokenAsync) before
   * joining the SignalR hub — no link, no banner, this is only ever visible as "it just works" when
   * the Dashboard has admin access (same-machine loopback bootstrap, see AdminTokenService). A
   * failed reissue (non-loopback host, no admin token available) is swallowed — falls back to
   * today's behavior (status still loads via admin fallback; hub/other per-project calls 401).
   */
  async init(projectId: string): Promise<void> {
    if (this._projectId() === projectId) {
      return;
    }

    if (this._projectId()) {
      void this.signalr.leaveProject(this._projectId());
    }

    if (!this.projectTokenService.getToken(projectId)) {
      await firstValueFrom(
        this.api.reissueToken(projectId).pipe(
          tap((dto) => {
            if (dto.token) {
              this.projectTokenService.setToken(projectId, dto.token);
            }

            if (dto.warning) {
              this.snackBar.open(dto.warning, 'Dismiss', { duration: 20000 });
            }
          }),
          catchError(() => of(null)),
        ),
      );
    }

    this._projectId.set(projectId);
    void this.signalr.joinProject(projectId);
  }

  refresh(): void {
    this.statusResource.reload();
  }

  triggerIndex(full: boolean): void {
    this._reindexing.set(true);
    this._reindexProgress.set(null);
    this.api.triggerIndex(this._projectId(), full).subscribe({
      next: () => this.snackBar.open(full ? 'Full reindex started…' : 'Reindexing changed files…', 'Dismiss', { duration: 4000 }),
      error: () => {
        this._reindexing.set(false);
        this.snackBar.open('Failed to start reindex — is Context.Host running?', 'Dismiss');
      },
    });
  }

  /**
   * Requests cancellation of the currently-running reindex. Doesn't flip `reindexing` locally on
   * success -- that happens when the ReindexProgress Completed:true/cancelled event this triggers
   * arrives (see the subscription above), the same way a normal completion is detected.
   * A 404 means the server has nothing running, so whatever progress is on screen is stale
   * (e.g. a run that died without sending its final event) and `onNotRunning` should clear it.
   */
  cancelIndex(onNotRunning?: () => void): void {
    this._cancelling.set(true);
    this.api.cancelIndex(this._projectId()).subscribe({
      next: () => {
        this._cancelling.set(false);
        this.snackBar.open('Cancelling reindex…', 'Dismiss', { duration: 3000 });
      },
      error: (err: { status?: number }) => {
        this._cancelling.set(false);
        if (err?.status === 404) {
          this._reindexing.set(false);
          this._reindexProgress.set(null);
          onNotRunning?.();
          this.snackBar.open('No reindex is currently running.', 'Dismiss', { duration: 4000 });
          return;
        }
        this.snackBar.open('Failed to cancel reindex.', 'Dismiss');
      },
    });
  }

  ngOnDestroy(): void {
    for (const subscription of this.subscriptions) {
      subscription.unsubscribe();
    }
    void this.signalr.leaveProject(this._projectId());
  }
}
