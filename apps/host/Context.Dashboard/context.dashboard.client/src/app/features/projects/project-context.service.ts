import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, firstValueFrom, of, tap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ProjectTokenService } from '../../core/project-token.service';
import { SignalrService } from '../../core/signalr.service';
import { ReindexProgressEvent } from '../../core/models';

/**
 * Scoped to the /projects/:id route subtree — see ProjectDetailComponent's `providers` array.
 * Every panel routed under it (overview/search/memories/sessions/graph) injects this instead of
 * re-reading the route param and re-fetching status independently. Auto-refreshes on the
 * IndexingHub's ProjectStatusChanged push, so a reindex triggered from the Overview tab updates
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
  private readonly _reindexProgress = signal<ReindexProgressEvent | null>(null);

  readonly projectId = this._projectId.asReadonly();

  private readonly statusResource = this.api.projectStatusResource(this._projectId);
  readonly status = computed(() => (this.statusResource.hasValue() ? this.statusResource.value() : null));
  /** True while there's no project id yet (nothing to show) or the status fetch is in flight. */
  readonly loading = computed(() => !this._projectId() || this.statusResource.isLoading());
  readonly projectName = computed(() => this.status()?.project.name ?? '');
  /** True from the moment a reindex is triggered until its ReindexProgress reports completed. */
  readonly reindexing = this._reindexing.asReadonly();
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
        this.snackBar.open('Reindex complete.', 'Dismiss', { duration: 4000 });
      }
    }),
  ];

  /**
   * When this browser has no stored bearer token for `projectId` (never captured a deep link, or
   * lost sessionStorage — see ProjectTokenService's own remarks), silently mints a replacement via
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

    void this.signalr.leaveProject(this._projectId());

    if (!this.projectTokenService.getToken(projectId)) {
      await firstValueFrom(
        this.api.reissueToken(projectId).pipe(
          tap((dto) => {
            if (dto.token) {
              this.projectTokenService.setToken(projectId, dto.token);
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

  ngOnDestroy(): void {
    for (const subscription of this.subscriptions) {
      subscription.unsubscribe();
    }
    void this.signalr.leaveProject(this._projectId());
  }
}
