import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { switchMap, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ReindexProgressEvent, SystemStatusDto } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

const POLL_INTERVAL_MS = 3000;

// How long a completed/idle reindex progress bar stays visible before it's cleared, so a
// 100% bar (or one that just stops updating, e.g. a dropped connection) doesn't linger forever.
const PROGRESS_HIDE_DELAY_MS = 2000;
const PROGRESS_IDLE_TIMEOUT_MS = 15000;

/**
 * System/health tab (CONTRACTS.md §12): host liveness + job queue depth from
 * GET /api/system/status (polled — this data has no push channel), plus a live reindex
 * progress bar fed by the IndexingHub's "ReindexProgress" SignalR event for the current project.
 */
@Component({
  selector: 'app-system-panel',
  imports: [CommonModule, MatCardModule, MatIconModule, MatProgressBarModule],
  templateUrl: './system-panel.component.html',
  styleUrl: './system-panel.component.scss',
})
export class SystemPanelComponent {
  private readonly api = inject(ApiService);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly status = signal<SystemStatusDto | null>(null);
  protected readonly statusError = signal(false);

  protected readonly reindexProgress = signal<ReindexProgressEvent | null>(null);
  protected readonly showReindexProgress = signal(false);

  private hideTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    // No push channel for host/job-queue stats (unlike ProjectStatusChanged) — poll it.
    timer(0, POLL_INTERVAL_MS)
      .pipe(
        switchMap(() => this.api.getSystemStatus()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (status) => {
          this.status.set(status);
          this.statusError.set(false);
        },
        error: () => this.statusError.set(true),
      });

    // ProjectContextService already joins/leaves this project's SignalR group for the whole
    // /projects/:id subtree (see its init/ngOnDestroy) — nothing more to wire up here.
    this.signalr.reindexProgress$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.projectId !== this.ctx.projectId()) {
        return;
      }
      this.reindexProgress.set(event);
      this.showReindexProgress.set(true);
      clearTimeout(this.hideTimer);
      this.hideTimer = setTimeout(
        () => this.showReindexProgress.set(false),
        event.completed ? PROGRESS_HIDE_DELAY_MS : PROGRESS_IDLE_TIMEOUT_MS,
      );
    });

    this.destroyRef.onDestroy(() => clearTimeout(this.hideTimer));
  }

  protected formatUptime(seconds: number): string {
    const totalMinutes = Math.floor(seconds / 60);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    if (hours <= 0) {
      return `${minutes}m`;
    }
    return `${hours}h ${minutes}m`;
  }

  protected reindexPercent(progress: ReindexProgressEvent): number {
    return progress.filesTotal > 0 ? Math.min(100, Math.round((progress.filesProcessed / progress.filesTotal) * 100)) : 0;
  }
}
