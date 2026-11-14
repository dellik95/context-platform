import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api.service';
import { JobDto, JobType, SystemStatusDto, USER_DATA_JOB_TYPES } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { CollapsibleSectionComponent } from '../../shared/ui/collapsible-section.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';
import { JobQueueKpisComponent } from './job-queue-kpis.component';

const JOB_PANEL_RELOAD_MIN_MS = 3000;

interface ThrottledReloader {
  schedule(): void;
  runNow(): void;
}

function createThrottledReloader(
  minIntervalMs: number,
  destroyRef: DestroyRef,
  callback: () => void,
): ThrottledReloader {
  let lastRunMs = 0;
  let pendingTimer: ReturnType<typeof setTimeout> | undefined;

  const clear = () => {
    clearTimeout(pendingTimer);
    pendingTimer = undefined;
  };

  destroyRef.onDestroy(clear);

  const runNow = () => {
    clear();
    lastRunMs = Date.now();
    callback();
  };

  const schedule = () => {
    const elapsed = Date.now() - lastRunMs;
    if (elapsed >= minIntervalMs) {
      runNow();
      return;
    }
    if (!pendingTimer) {
      pendingTimer = setTimeout(runNow, minIntervalMs - elapsed);
    }
  };

  return { schedule, runNow };
}

@Component({
  selector: 'app-dashboard-jobs-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    CollapsibleSectionComponent,
    BadgeComponent,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
    JobQueueKpisComponent,
  ],
  templateUrl: './dashboard-jobs-card.component.html',
})
export class DashboardJobsCardComponent {
  private readonly api = inject(ApiService);
  private readonly signalr = inject(SignalrService);
  private readonly toast = inject(ToastService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  readonly status = input<SystemStatusDto | null>(null);

  protected readonly failedExpanded = signal(true);
  protected readonly pendingExpanded = signal(false);
  protected readonly runningExpanded = signal(false);

  private readonly failedJobsResource = this.api.jobsResource(this.ctx.projectId, 'Failed');
  protected readonly failedJobs = computed(() => (this.failedJobsResource.hasValue() ? this.failedJobsResource.value() : []));
  protected readonly jobsLoading = this.failedJobsResource.isLoading;
  protected readonly initialJobsLoading = computed(() => this.failedJobsResource.status() === 'loading');
  protected readonly jobsError = computed(() => Boolean(this.failedJobsResource.error()));
  protected readonly retryingJobId = signal<string | null>(null);
  protected readonly retryingAll = signal(false);
  protected readonly cleaningUpDone = signal(false);

  private readonly runningJobsRequested = signal(false);
  private readonly runningJobsResource = this.api.jobsResource(this.ctx.projectId, 'Running', this.runningJobsRequested);
  protected readonly runningJobs = computed(() => (this.runningJobsResource.hasValue() ? this.runningJobsResource.value() : []));
  protected readonly runningJobsLoading = this.runningJobsResource.isLoading;
  protected readonly initialRunningJobsLoading = computed(() => this.runningJobsResource.status() === 'loading');
  protected readonly runningJobsError = computed(() => Boolean(this.runningJobsResource.error()));
  protected readonly forcingJobId = signal<string | null>(null);
  protected readonly forcingAll = signal(false);
  protected readonly cleaningUpPending = signal(false);

  private readonly pendingByTypeRequested = signal(false);
  private readonly pendingByTypeResource = this.api.jobCountsByTypeResource(
    this.ctx.projectId,
    'Pending',
    this.pendingByTypeRequested,
  );
  protected readonly pendingByType = computed(() =>
    this.pendingByTypeResource.hasValue() ? this.pendingByTypeResource.value() : [],
  );
  protected readonly pendingByTypeLoading = this.pendingByTypeResource.isLoading;
  protected readonly initialPendingByTypeLoading = computed(() => this.pendingByTypeResource.status() === 'loading');
  protected readonly pendingByTypeError = computed(() => Boolean(this.pendingByTypeResource.error()));
  protected readonly cleaningUpPendingType = signal<JobType | null>(null);

  private readonly runningReloader = createThrottledReloader(JOB_PANEL_RELOAD_MIN_MS, this.destroyRef, () => {
    if (this.runningJobsRequested()) {
      this.runningJobsResource.reload();
    } else {
      this.runningJobsRequested.set(true);
    }
  });

  private readonly pendingReloader = createThrottledReloader(JOB_PANEL_RELOAD_MIN_MS, this.destroyRef, () => {
    if (this.pendingByTypeRequested()) {
      this.pendingByTypeResource.reload();
    } else {
      this.pendingByTypeRequested.set(true);
    }
  });

  constructor() {
    effect(() => {
      if (this.pendingExpanded() && !this.pendingByTypeRequested()) {
        this.loadPendingByType();
      }
    });

    effect(() => {
      if (this.runningExpanded() && !this.runningJobsRequested()) {
        this.loadRunningJobs();
      }
    });

    this.signalr.jobCountsChanged$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((counts) => {
      const current = this.status();
      if (current?.jobsFailed !== counts.failed && this.failedJobsResource.hasValue()) {
        this.failedJobsResource.reload();
      }
      if (current?.jobsRunning !== counts.running && this.runningJobsRequested()) {
        this.runningReloader.schedule();
      }
      if (current?.jobsPending !== counts.pending && this.pendingByTypeRequested()) {
        this.pendingReloader.schedule();
      }
    });
  }

  protected loadFailedJobs(): void {
    this.failedJobsResource.reload();
  }

  protected loadPendingByType(): void {
    this.pendingReloader.runNow();
  }

  protected loadRunningJobs(): void {
    this.runningReloader.runNow();
  }

  protected isUserDataJobType(type: JobType): boolean {
    return USER_DATA_JOB_TYPES.includes(type);
  }

  protected retryJob(job: JobDto): void {
    this.retryingJobId.set(job.id);
    this.api.retryJob(job.id).subscribe({
      next: () => {
        this.retryingJobId.set(null);
        this.failedJobsResource.update((jobs) => (jobs ?? []).filter((j) => j.id !== job.id));
        this.toast.success(`Retry queued for ${job.type} job.`);
      },
      error: (err) => {
        this.retryingJobId.set(null);
        this.toast.error(err?.error?.message ?? `Failed to retry ${job.type} job.`);
      },
    });
  }

  protected retryAllFailed(): void {
    this.retryingAll.set(true);
    this.api.retryAllFailedJobs(this.ctx.projectId()).subscribe({
      next: (result) => {
        this.retryingAll.set(false);
        this.failedJobsResource.reload();
        this.toast.success(`Retried ${result.retried}/${result.total} failed job(s).`);
      },
      error: () => {
        this.retryingAll.set(false);
        this.toast.error('Failed to retry jobs.');
      },
    });
  }

  protected async cleanupPendingOfType(type: JobType, count: number): Promise<void> {
    const confirmed = await this.confirmDialog.open({
      title: `Delete pending ${type} jobs`,
      message: `Delete ${count} pending ${type} job(s)? This discards queued work that has not run yet.`,
      confirmLabel: 'Delete jobs',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.cleaningUpPendingType.set(type);
    this.api.cleanupJobs(this.ctx.projectId(), 'Pending', type).subscribe({
      next: (result) => {
        this.cleaningUpPendingType.set(null);
        this.loadPendingByType();
        this.toast.success(`Removed ${result.removed} pending ${type} job(s) from the queue.`);
      },
      error: () => {
        this.cleaningUpPendingType.set(null);
        this.toast.error(`Failed to clear pending ${type} jobs.`);
      },
    });
  }

  protected forceJob(job: JobDto): void {
    this.forcingJobId.set(job.id);
    this.api.forceJob(job.id).subscribe({
      next: () => {
        this.forcingJobId.set(null);
        this.loadRunningJobs();
        this.toast.info(`Force-started ${job.type} job.`);
      },
      error: (err) => {
        this.forcingJobId.set(null);
        this.toast.error(err?.error?.message ?? `Failed to force ${job.type} job.`);
      },
    });
  }

  protected async forceAllRunning(): Promise<void> {
    const confirmed = await this.confirmDialog.open({
      title: 'Force restart running jobs',
      message: 'Cancel and restart every Running job now? Their current attempts will be abandoned.',
      confirmLabel: 'Force restart',
      confirmTone: 'warning',
    });
    if (!confirmed) {
      return;
    }

    this.forcingAll.set(true);
    this.api.forceRunningJobs(this.ctx.projectId()).subscribe({
      next: (result) => {
        this.forcingAll.set(false);
        this.loadRunningJobs();
        this.toast.success(`Force-started ${result.forced}/${result.total} running job(s).`);
      },
      error: () => {
        this.forcingAll.set(false);
        this.toast.error('Failed to force running jobs.');
      },
    });
  }

  protected cleanupCompleted(): void {
    this.cleaningUpDone.set(true);
    this.api.cleanupJobs(this.ctx.projectId(), 'Done').subscribe({
      next: (result) => {
        this.cleaningUpDone.set(false);
        this.toast.success(`Removed ${result.removed} completed job(s) from the queue.`);
      },
      error: () => {
        this.cleaningUpDone.set(false);
        this.toast.error('Failed to clean up the job queue.');
      },
    });
  }

  protected async cleanupPending(): Promise<void> {
    const confirmed = await this.confirmDialog.open({
      title: 'Clear pending queue',
      message:
        'Delete every currently Pending job? This discards queued work that has not run yet. Hook, memory and summary jobs are kept.',
      confirmLabel: 'Delete pending jobs',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.cleaningUpPending.set(true);
    this.api.cleanupJobs(this.ctx.projectId(), 'Pending').subscribe({
      next: (result) => {
        this.cleaningUpPending.set(false);
        this.toast.success(`Removed ${result.removed} pending job(s) from the queue.`);
      },
      error: () => {
        this.cleaningUpPending.set(false);
        this.toast.error('Failed to clear pending jobs.');
      },
    });
  }
}
