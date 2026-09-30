import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/api.service';
import { HookInvocationDto, JobDto, JobType, ReindexProgressEvent } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ProjectContextService } from '../projects/project-context.service';
import { LogsDialogComponent } from './logs-dialog.component';

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

// Running/Pending counts move on every job transition, so an open Running or Pending panel refetches at most this often.
const JOB_PANEL_RELOAD_MIN_MS = 3000;

/**
 * Per-project Dashboard tab (CONTRACTS.md §12; merged with the former Overview tab): project-level
 * stats and reindex trigger buttons (GET /api/projects/{id}/status plus triggerIndex/cancelIndex,
 * both via ProjectContextService), alongside host liveness from GET /api/system/status (fetched
 * once, then only reconciled on a slow fallback interval — see STATUS_RECONCILE_INTERVAL_MS), job
 * queue depth updated live via the IndexingHub's "JobCountsChanged" SignalR push (patched into the
 * existing statusResource value in place — no refetch, no loading flicker), and a live reindex
 * progress bar fed by the "ReindexProgress" SignalR event for the current project. Also hosts
 * operational drill-down that the live counts above don't provide: a Failed jobs table
 * (GET /api/jobs, scoped to the current project; POST /api/jobs/{id}/retry), plus a Logs button opening
 * the host-wide log tail dialog.
 *
 * The reindex progress card below is this component's own SignalR-driven tracker rather than
 * ctx.reindexing()/ctx.reindexProgress() -- ctx only flips reindexing=true when *this* browser
 * session called triggerIndex(), so it can't reflect a reindex started elsewhere (another tab,
 * another user), while this component's tracker reacts to any ReindexProgress event for the
 * project regardless of who started it. ctx.reindexing() is still used below to disable the
 * trigger buttons, since that part genuinely is about this session's own in-flight request.
 */
@Component({
  selector: 'app-dashboard-panel',
  imports: [
    CommonModule,
    MatButtonModule,
    MatCardModule,
    MatDialogModule,
    MatExpansionModule,
    MatIconModule,
    MatProgressBarModule,
    MatTooltipModule,
  ],
  templateUrl: './dashboard-panel.component.html',
  styleUrl: './dashboard-panel.component.scss',
})
export class DashboardPanelComponent {
  private readonly api = inject(ApiService);
  private readonly signalr = inject(SignalrService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
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

  // Failed-jobs table: loaded on tab load + manual refresh, and kept live thereafter -- the
  // constructor's jobCountsChanged$ subscription reloads it whenever the pushed Failed count
  // actually moves, so it doesn't refetch on every unrelated pending/running transition.
  private readonly failedJobsResource = this.api.jobsResource(this.ctx.projectId, 'Failed');
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
  protected readonly cleaningUpDone = signal(false);

  // Running-jobs table: fetched once its panel is first opened, then refreshed on Running count pushes.
  private readonly runningJobsRequested = signal(false);
  private readonly runningJobsResource = this.api.jobsResource(this.ctx.projectId, 'Running', this.runningJobsRequested);
  protected readonly runningJobs = computed(() => (this.runningJobsResource.hasValue() ? this.runningJobsResource.value() : []));
  protected readonly runningJobsLoading = this.runningJobsResource.isLoading;
  protected readonly runningJobsError = computed(() => Boolean(this.runningJobsResource.error()));
  protected readonly forcingJobId = signal<string | null>(null);
  protected readonly forcingAll = signal(false);
  private lastRunningReloadMs = 0;
  protected readonly cleaningUpPending = signal(false);

  // Pending breakdown by job type: fetched once its panel is first opened, then refreshed on Pending count pushes.
  private readonly pendingByTypeRequested = signal(false);
  private readonly pendingByTypeResource = this.api.jobCountsByTypeResource(this.ctx.projectId, 'Pending', this.pendingByTypeRequested);
  protected readonly pendingByType = computed(() => (this.pendingByTypeResource.hasValue() ? this.pendingByTypeResource.value() : []));
  protected readonly pendingByTypeLoading = this.pendingByTypeResource.isLoading;
  protected readonly pendingByTypeError = computed(() => Boolean(this.pendingByTypeResource.error()));
  protected readonly cleaningUpPendingType = signal<JobType | null>(null);
  private lastPendingReloadMs = 0;

  // Hook invocations: loaded once on tab mount, then kept live by the "HookInvoked" SignalR push
  // below (patched into the existing resource value in place — no refetch, same pattern as the
  // job-counts patch above).
  private readonly hooksStatusResource = this.api.hooksStatusResource(this.ctx.projectId);
  protected readonly hookCount = computed(() => (this.hooksStatusResource.hasValue() ? this.hooksStatusResource.value()?.count ?? 0 : 0));
  protected readonly recentHooks = computed(() => (this.hooksStatusResource.hasValue() ? this.hooksStatusResource.value()?.recent ?? [] : []));
  protected readonly hooksLoading = this.hooksStatusResource.isLoading;
  protected readonly initialHooksLoading = computed(() => this.hooksLoading() && !this.hooksStatusResource.hasValue());
  protected readonly hooksError = computed(() => Boolean(this.hooksStatusResource.error()));

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
      const failedCountChanged = this.status()?.jobsFailed !== counts.failed;
      const runningCountChanged = this.status()?.jobsRunning !== counts.running;
      const pendingCountChanged = this.status()?.jobsPending !== counts.pending;

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

      // Failed jobs table: the count push above tells us the Failed total moved, so refetch the
      // table itself to match -- only once it's been loaded at least once (tab visited), same
      // lazy-load gate as everywhere else on this page.
      if (failedCountChanged && this.failedJobsResource.hasValue()) {
        this.failedJobsResource.reload();
      }

      if (runningCountChanged && this.runningJobsRequested() && Date.now() - this.lastRunningReloadMs >= JOB_PANEL_RELOAD_MIN_MS) {
        this.loadRunningJobs();
      }

      if (pendingCountChanged && this.pendingByTypeRequested() && Date.now() - this.lastPendingReloadMs >= JOB_PANEL_RELOAD_MIN_MS) {
        this.loadPendingByType();
      }
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

    // Project-scoped (ProjectContextService already joins/leaves this project's SignalR group for
    // the whole /projects/:id subtree) — patches the count/recent list in place, no refetch.
    this.signalr.hookInvoked$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((evt) => {
      this.hooksStatusResource.update((current) => {
        if (!current) {
          return current;
        }

        const invocation: HookInvocationDto = {
          id: crypto.randomUUID(),
          type: evt.type,
          payloadJson: evt.payloadJson,
          receivedAt: evt.receivedAt,
        };

        return { count: evt.totalCount, recent: [invocation, ...current.recent].slice(0, 10) };
      });
    });
  }

  protected reindex(full: boolean): void {
    this.ctx.triggerIndex(full);
  }

  protected cancelIndex(): void {
    this.ctx.cancelIndex(() => this.showReindexProgress.set(false));
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

  protected loadHooksStatus(): void {
    this.hooksStatusResource.reload();
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

  protected loadPendingByType(): void {
    this.lastPendingReloadMs = Date.now();
    if (this.pendingByTypeRequested()) {
      this.pendingByTypeResource.reload();
    } else {
      this.pendingByTypeRequested.set(true);
    }
  }

  protected cleanupPendingOfType(type: JobType, count: number): void {
    if (!confirm(`Delete ${count} pending ${type} job(s)? This discards queued work that has not run yet.`)) {
      return;
    }

    this.cleaningUpPendingType.set(type);
    this.api.cleanupJobs(this.ctx.projectId(), 'Pending', type).subscribe({
      next: (result) => {
        this.cleaningUpPendingType.set(null);
        this.loadPendingByType();
        this.snackBar.open(`Removed ${result.removed} pending ${type} job(s) from the queue.`, 'Dismiss');
      },
      error: () => {
        this.cleaningUpPendingType.set(null);
        this.snackBar.open(`Failed to clear pending ${type} jobs.`, 'Dismiss');
      },
    });
  }

  protected loadRunningJobs(): void {
    this.lastRunningReloadMs = Date.now();
    if (this.runningJobsRequested()) {
      this.runningJobsResource.reload();
    } else {
      this.runningJobsRequested.set(true);
    }
  }

  protected forceJob(job: JobDto): void {
    this.forcingJobId.set(job.id);
    this.api.forceJob(job.id).subscribe({
      next: () => {
        this.forcingJobId.set(null);
        this.loadRunningJobs();
        this.snackBar.open(`Force-started ${job.type} job.`, 'Dismiss');
      },
      error: (err) => {
        this.forcingJobId.set(null);
        this.snackBar.open(err?.error?.message ?? `Failed to force ${job.type} job.`, 'Dismiss');
      },
    });
  }

  /** Cancels every in-flight Running job of the project and restarts each outside the lane limit. */
  protected forceAllRunning(): void {
    if (!confirm('Cancel and restart every Running job now? Their current attempts are abandoned.')) {
      return;
    }

    this.forcingAll.set(true);
    this.api.forceRunningJobs(this.ctx.projectId()).subscribe({
      next: (result) => {
        this.forcingAll.set(false);
        this.loadRunningJobs();
        this.snackBar.open(`Force-started ${result.forced}/${result.total} running job(s).`, 'Dismiss');
      },
      error: () => {
        this.forcingAll.set(false);
        this.snackBar.open('Failed to force running jobs.', 'Dismiss');
      },
    });
  }

  protected cleanupCompleted(): void {
    this.cleaningUpDone.set(true);
    this.api.cleanupJobs(this.ctx.projectId(), 'Done').subscribe({
      next: (result) => {
        this.cleaningUpDone.set(false);
        this.snackBar.open(`Removed ${result.removed} completed job(s) from the queue.`, 'Dismiss');
      },
      error: () => {
        this.cleaningUpDone.set(false);
        this.snackBar.open('Failed to clean up the job queue.', 'Dismiss');
      },
    });
  }

  /**
   * Clears the Pending backlog outright (e.g. thousands of doomed retries queued by a since-fixed
   * config bug) rather than waiting for them to be attempted and either succeed or eventually fail.
   * Unlike "clean up completed", this can discard real not-yet-attempted work, so it gets its own
   * explicit action and confirmation instead of being folded into the Done cleanup.
   */
  protected cleanupPending(): void {
    if (!confirm('Delete every currently Pending job? This discards queued work that has not run yet.')) {
      return;
    }

    this.cleaningUpPending.set(true);
    this.api.cleanupJobs(this.ctx.projectId(), 'Pending').subscribe({
      next: (result) => {
        this.cleaningUpPending.set(false);
        this.snackBar.open(`Removed ${result.removed} pending job(s) from the queue.`, 'Dismiss');
      },
      error: () => {
        this.cleaningUpPending.set(false);
        this.snackBar.open('Failed to clear pending jobs.', 'Dismiss');
      },
    });
  }

  protected openLogs(): void {
    this.dialog.open(LogsDialogComponent, { width: '1100px', maxWidth: '95vw', maxHeight: '90vh' });
  }
}
