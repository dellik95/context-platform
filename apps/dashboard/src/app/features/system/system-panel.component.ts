import { CommonModule } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { switchMap, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { JobDto, LogTailDto, ReindexProgressEvent, SystemStatusDto } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

// Truncation length for a Failed job's LastError shown inline in the table -- the full text is
// still available via the cell's matTooltip, this just keeps one row from blowing out the table.
const LAST_ERROR_PREVIEW_LENGTH = 140;

const POLL_INTERVAL_MS = 3000;

// How long a completed/idle reindex progress bar stays visible before it's cleared, so a
// 100% bar (or one that just stops updating, e.g. a dropped connection) doesn't linger forever.
const PROGRESS_HIDE_DELAY_MS = 2000;
const PROGRESS_IDLE_TIMEOUT_MS = 15000;

/**
 * System/health tab (CONTRACTS.md §12): host liveness + job queue depth from
 * GET /api/system/status (polled — this data has no push channel), plus a live reindex
 * progress bar fed by the IndexingHub's "ReindexProgress" SignalR event for the current project.
 * Also hosts operational drill-down that the 3s-polled aggregate counts don't provide: a Failed
 * jobs table (GET /api/jobs, POST /api/jobs/{id}/retry) and an on-demand host log tail
 * (GET /api/logs/tail) — both host-wide (not scoped to the current project), loaded independently
 * of the status poll above.
 */
@Component({
  selector: 'app-system-panel',
  imports: [
    CommonModule,
    MatButtonModule,
    MatCardModule,
    MatExpansionModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
  ],
  templateUrl: './system-panel.component.html',
  styleUrl: './system-panel.component.scss',
})
export class SystemPanelComponent {
  private readonly api = inject(ApiService);
  private readonly signalr = inject(SignalrService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  protected readonly status = signal<SystemStatusDto | null>(null);
  protected readonly statusError = signal(false);

  protected readonly reindexProgress = signal<ReindexProgressEvent | null>(null);
  protected readonly showReindexProgress = signal(false);

  // Failed-jobs table: loaded once on tab load + manual refresh, deliberately NOT part of the 3s
  // status poll above -- job failures don't need that aggressive a refresh rate, and refetching a
  // potentially-growing table every 3s would be wasteful.
  protected readonly failedJobs = signal<JobDto[]>([]);
  protected readonly jobsLoading = signal(true);
  protected readonly jobsError = signal(false);
  protected readonly retryingJobId = signal<string | null>(null);

  // Log tail: fetched on demand (button click) rather than eagerly, since most visits to this tab
  // don't need it and a 200-line tail on every load would be wasted work.
  protected readonly logLines = signal<string[] | null>(null);
  protected readonly logFilePath = signal<string | null>(null);
  protected readonly logsLoading = signal(false);

  private hideTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    this.loadFailedJobs();

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

  protected loadFailedJobs(): void {
    this.jobsLoading.set(true);
    this.jobsError.set(false);
    this.api.listJobs('Failed').subscribe({
      next: (jobs) => {
        this.failedJobs.set(jobs);
        this.jobsLoading.set(false);
      },
      error: () => {
        this.failedJobs.set([]);
        this.jobsLoading.set(false);
        this.jobsError.set(true);
      },
    });
  }

  protected retryJob(job: JobDto): void {
    this.retryingJobId.set(job.id);
    this.api.retryJob(job.id).subscribe({
      next: () => {
        this.retryingJobId.set(null);
        this.failedJobs.update((jobs) => jobs.filter((j) => j.id !== job.id));
        this.snackBar.open(`Retry queued for ${job.type} job.`, 'Dismiss');
      },
      error: (err) => {
        this.retryingJobId.set(null);
        const message = err?.error?.message ?? `Failed to retry ${job.type} job.`;
        this.snackBar.open(message, 'Dismiss');
      },
    });
  }

  protected truncateError(error: string | null): string {
    if (!error) {
      return '';
    }
    return error.length > LAST_ERROR_PREVIEW_LENGTH
      ? `${error.slice(0, LAST_ERROR_PREVIEW_LENGTH)}…`
      : error;
  }

  protected loadLogTail(): void {
    this.logsLoading.set(true);
    this.api.getLogTail().subscribe({
      next: (tail: LogTailDto) => {
        this.logLines.set(tail.lines);
        this.logFilePath.set(tail.filePath);
        this.logsLoading.set(false);
      },
      error: () => {
        this.logsLoading.set(false);
        this.snackBar.open('Failed to load log tail — is Context.Host running?', 'Dismiss');
      },
    });
  }
}
