import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/api.service';
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
        this.snackBar.open(event.cancelled ? 'Reindex cancelled.' : 'Reindex complete.', 'Dismiss', { duration: 4000 });
      }
    }),
  ];

  init(projectId: string): void {
    if (this._projectId() === projectId) {
      return;
    }

    void this.signalr.leaveProject(this._projectId());
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
   */
  cancelIndex(): void {
    this.api.cancelIndex(this._projectId()).subscribe({
      next: () => this.snackBar.open('Cancelling reindex…', 'Dismiss', { duration: 3000 }),
      error: (err: { status?: number }) => {
        const message = err?.status === 404 ? 'No reindex is currently running.' : 'Failed to cancel reindex.';
        this.snackBar.open(message, 'Dismiss');
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
