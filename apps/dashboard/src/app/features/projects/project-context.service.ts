import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { SignalrService } from '../../core/signalr.service';
import { ProjectStatusDto } from '../../core/models';

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

  private readonly _projectId = signal<string>('');
  private readonly _status = signal<ProjectStatusDto | null>(null);
  private readonly _loading = signal(true);

  readonly projectId = this._projectId.asReadonly();
  readonly status = this._status.asReadonly();
  readonly loading = this._loading.asReadonly();
  readonly projectName = computed(() => this._status()?.project.name ?? '');

  private subscription = this.signalr.projectStatusChanged$.subscribe((dto) => {
    if (dto.project.id === this._projectId()) {
      this._status.set(dto);
    }
  });

  init(projectId: string): void {
    if (this._projectId() === projectId) {
      return;
    }

    void this.signalr.leaveProject(this._projectId());
    this._projectId.set(projectId);
    void this.signalr.joinProject(projectId);
    this.refresh();
  }

  refresh(): void {
    this._loading.set(true);
    this.api.getProjectStatus(this._projectId()).subscribe({
      next: (status) => {
        this._status.set(status);
        this._loading.set(false);
      },
      error: () => this._loading.set(false),
    });
  }

  triggerIndex(full: boolean): void {
    this.api.triggerIndex(this._projectId(), full).subscribe();
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
    void this.signalr.leaveProject(this._projectId());
  }
}
