import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { catchError, firstValueFrom, of, tap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ReindexProgressEvent } from '../../core/models';
import { ProjectTokenService } from '../../core/project-token.service';
import { SignalrService } from '../../core/signalr.service';
import { ToastService } from '../../shared/ui/toast.service';

/**
 * Scoped to the /projects/:id route subtree. Panels under it inject this service rather than
 * re-reading route params or polling status independently.
 */
@Injectable()
export class ProjectContextService implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly projectTokenService = inject(ProjectTokenService);
  private readonly signalr = inject(SignalrService);
  private readonly toast = inject(ToastService);

  private readonly _projectId = signal<string>('');
  private readonly _reindexing = signal(false);
  private readonly _cancelling = signal(false);
  private readonly _reindexProgress = signal<ReindexProgressEvent | null>(null);

  readonly projectId = this._projectId.asReadonly();

  private readonly statusResource = this.api.projectStatusResource(this._projectId);
  readonly status = computed(() => (this.statusResource.hasValue() ? this.statusResource.value() : null));
  readonly loading = computed(() => !this._projectId() || this.statusResource.isLoading());
  readonly projectName = computed(() => this.status()?.project.name ?? '');
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
        if (event.failed) {
          this.toast.error('Reindex failed — see host logs.');
        } else if (event.cancelled) {
          this.toast.warning('Reindex cancelled.', 4000);
        } else {
          this.toast.success('Reindex complete.', 4000);
        }
      }
    }),
  ];

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
              this.toast.warning(dto.warning, 20000);
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
      next: () => this.toast.show(full ? 'Full reindex started…' : 'Reindexing changed files…', 'info', 4000),
      error: () => {
        this._reindexing.set(false);
        this.toast.error('Failed to start reindex — is Context.Dashboard.Server running?');
      },
    });
  }

  cancelIndex(onNotRunning?: () => void): void {
    this._cancelling.set(true);
    this.api.cancelIndex(this._projectId()).subscribe({
      next: () => {
        this._cancelling.set(false);
        this.toast.show('Cancelling reindex…', 'info', 3000);
      },
      error: (err: { status?: number }) => {
        this._cancelling.set(false);
        if (err?.status === 404) {
          this._reindexing.set(false);
          this._reindexProgress.set(null);
          onNotRunning?.();
          this.toast.show('No reindex is currently running.', 'info', 4000);
          return;
        }
        this.toast.error('Failed to cancel reindex.');
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
