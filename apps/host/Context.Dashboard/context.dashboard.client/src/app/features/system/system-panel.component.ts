import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/api.service';
import { JobDto, ReindexProgressEvent } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';

// Truncation length for a Failed job's LastError shown inline in the table -- the full text is
// still available via the cell's matTooltip, this just keeps one row from blowing out the table.
const LAST_ERROR_PREVIEW_LENGTH = 140;

// Job counts (pending/running/failed/done) update live over the "JobCountsChanged" SignalR push
// (see the constructor) -- this interval is only a reconciliation fallback for the rest of the
// DTO (db/provider/mcp liveness) in case a push was ever missed (e.g. a dropped hub connection),
// and for db/provider/mcp liveness which has no push channel of its own. Both rarely change
// outside a restart, so this can be a lot slower than the old 3s job-count poll it replaces.
const STATUS_RECONCILE_INTERVAL_MS = 120_000;

// Purely local, network-free tick driving the live-incrementing uptime display below.
const UPTIME_TICK_MS = 1000;

// How long a completed/idle reindex progress bar stays visible before it's cleared, so a
// 100% bar (or one that just stops updating, e.g. a dropped connection) doesn't linger forever.
const PROGRESS_HIDE_DELAY_MS = 2000;
const PROGRESS_IDLE_TIMEOUT_MS = 15000;

/**
 * System/health tab (CONTRACTS.md §12): host liveness from GET /api/system/status (fetched once,
 * then only reconciled on a slow fallback interval — see STATUS_RECONCILE_INTERVAL_MS), job queue
 * depth updated live via the IndexingHub's "JobCountsChanged" SignalR push (patched into the
 * existing statusResource value in place — no refetch, no loading flicker), and a live reindex
 * progress bar fed by the "ReindexProgress" SignalR event for the current project. Also hosts
 * operational drill-down that the live counts above don't provide: a Failed jobs table
 * (GET /api/jobs, scoped to the current project; POST /api/jobs/{id}/retry) and an on-demand host
 * log tail (GET /api/logs/tail, host-wide) — loaded independently of the status above.
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

  // Fetched once at tick=0, then only reconciled on a slow fallback interval (see
  // STATUS_RECONCILE_INTERVAL_MS) -- job counts within it are kept fresh live by the
  // "JobCountsChanged" SignalR push instead (patched in place below, no refetch).
  private readonly pollTick = signal(0);
  private readonly statusResource = this.api.systemStatusResource(this.pollTick);
  protected readonly status = computed(() => (this.statusResource.hasValue() ? this.statusResource.value() : null));
  protected readonly statusError = computed(() => Boolean(this.statusResource.error()));

  // Live-incrementing uptime display, computed client-side from a baseline captured once from the
  // server's own uptimeSeconds (see the constructor) plus wall-clock time elapsed since -- avoids
  // polling the network just to advance a number that only the host's own clock determines.
  private readonly clockTick = signal(0);
  private readonly hostStartMs = signal<number | null>(null);
  protected readonly displayUptimeSeconds = computed(() => {
    this.clockTick();
    const startMs = this.hostStartMs();
    return startMs === null ? null : (Date.now() - startMs) / 1000;
  });

  protected readonly reindexProgress = signal<ReindexProgressEvent | null>(null);
  protected readonly showReindexProgress = signal(false);

  // Failed-jobs table: loaded once on tab load + manual refresh, deliberately NOT part of any
  // status poll/push above -- job failures don't need that aggressive a refresh rate, and
  // refetching a potentially-growing table on every job-count change would be wasteful.
  private readonly failedJobsResource = this.api.failedJobsResource(this.ctx.projectId);
  protected readonly failedJobs = computed(() => (this.failedJobsResource.hasValue() ? this.failedJobsResource.value() : []));
  protected readonly jobsLoading = this.failedJobsResource.isLoading;
  // Gates the *whole table* being replaced by a spinner -- true only on the very first load, when
  // there's nothing to show yet. A manual "Refresh" reload keeps showing the existing rows (with
  // isLoading() still available to the template for a non-blocking indicator) instead of blanking
  // the panel, matching the same no-loader-on-a-live-update UX as the rest of this tab.
  protected readonly initialJobsLoading = computed(() => this.jobsLoading() && !this.failedJobsResource.hasValue());
  protected readonly jobsError = computed(() => Boolean(this.failedJobsResource.error()));
  protected readonly retryingJobId = signal<string | null>(null);
  protected readonly retryingAll = signal(false);
  protected readonly cleaningUp = signal(false);

  // Log tail: fetched on demand (button click) rather than eagerly, since most visits to this tab
  // don't need it and a 200-line tail on every load would be wasted work.
  private readonly logsRequested = signal(false);
  private readonly logResource = this.api.logTailResource(this.logsRequested);
  protected readonly logLines = computed(() => (this.logResource.hasValue() ? this.logResource.value().lines : null));
  protected readonly logFilePath = computed(() => (this.logResource.hasValue() ? this.logResource.value().filePath : null));
  protected readonly logsLoading = this.logResource.isLoading;
  // Same first-load-only gating as initialJobsLoading above -- a "Refresh" click keeps the
  // previous tail visible instead of blanking it while the new one streams in.
  protected readonly initialLogsLoading = computed(() => this.logsLoading() && !this.logResource.hasValue());

  private hideTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    const reconcileIntervalId = setInterval(() => this.pollTick.update((tick) => tick + 1), STATUS_RECONCILE_INTERVAL_MS);
    this.destroyRef.onDestroy(() => clearInterval(reconcileIntervalId));

    const clockIntervalId = setInterval(() => this.clockTick.update((tick) => tick + 1), UPTIME_TICK_MS);
    this.destroyRef.onDestroy(() => clearInterval(clockIntervalId));

    // Job counts arrive live here instead of via the poll above -- .update() patches them into
    // the existing statusResource value in place (no HTTP refetch, so isLoading() never flips and
    // no loading state/skeleton is shown for what is, from the user's perspective, just a number
    // ticking over). Global push (not scoped to the current project's SignalR group), matching
    // the counts themselves being host-wide.
    this.signalr.jobCountsChanged$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((counts) => {
      this.statusResource.update((current) =>
        current
          ? {
              ...current,
              jobsPending: counts.pending,
              jobsRunning: counts.running,
              jobsFailed: counts.failed,
              jobsDone: counts.done,
            }
          : current,
      );
    });

    // Resync the uptime baseline whenever the server's own uptimeSeconds actually changes (the
    // initial load, or the slow reconciliation poll) -- NOT on every statusResource identity
    // change, since the jobCountsChanged$ patch above also produces a new object but leaves
    // uptimeSeconds untouched. Reading only that one field (rather than the whole DTO) means
    // Angular's signal equality check skips this effect entirely when it hasn't actually changed.
    effect(() => {
      const seconds = this.status()?.uptimeSeconds;
      if (seconds !== undefined) {
        this.hostStartMs.set(Date.now() - seconds * 1000);
      }
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

    effect(() => {
      if (this.logResource.error()) {
        this.snackBar.open('Failed to load log tail — is Context.Host running?', 'Dismiss');
      }
    });
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
    this.failedJobsResource.reload();
  }

  protected retryJob(job: JobDto): void {
    this.retryingJobId.set(job.id);
    this.api.retryJob(job.id).subscribe({
      next: () => {
        this.retryingJobId.set(null);
        this.failedJobsResource.update((jobs) => (jobs ?? []).filter((j) => j.id !== job.id));
        this.snackBar.open(`Retry queued for ${job.type} job.`, 'Dismiss');
      },
      error: (err) => {
        this.retryingJobId.set(null);
        const message = err?.error?.message ?? `Failed to retry ${job.type} job.`;
        this.snackBar.open(message, 'Dismiss');
      },
    });
  }

  protected retryAllFailed(): void {
    this.retryingAll.set(true);
    this.api.retryAllFailedJobs(this.ctx.projectId()).subscribe({
      next: (result) => {
        this.retryingAll.set(false);
        this.failedJobsResource.reload();
        this.snackBar.open(`Retried ${result.retried}/${result.total} failed job(s).`, 'Dismiss');
      },
      error: () => {
        this.retryingAll.set(false);
        this.snackBar.open('Failed to retry jobs.', 'Dismiss');
      },
    });
  }

  protected cleanupCompleted(): void {
    this.cleaningUp.set(true);
    this.api.cleanupCompletedJobs(this.ctx.projectId()).subscribe({
      next: (result) => {
        this.cleaningUp.set(false);
        this.snackBar.open(`Removed ${result.removed} completed job(s) from the queue.`, 'Dismiss');
      },
      error: () => {
        this.cleaningUp.set(false);
        this.snackBar.open('Failed to clean up the job queue.', 'Dismiss');
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
    if (this.logsRequested()) {
      this.logResource.reload();
    } else {
      this.logsRequested.set(true);
    }
  }
}
