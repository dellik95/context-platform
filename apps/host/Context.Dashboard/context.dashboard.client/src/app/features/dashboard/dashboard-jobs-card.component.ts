import { CommonModule } from '@angular/common';
import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
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

const JOB_PANEL_RELOAD_MIN_MS = 3000;

@Component({
  selector: 'app-dashboard-jobs-card',
  imports: [
    CommonModule,
    CollapsibleSectionComponent,
    BadgeComponent,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
  ],
  template: `
    <div class="space-y-4">
      @if (status(); as hostStatus) {
        <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div class="flex items-start justify-between rounded-2xl border border-slate-200 bg-white p-4 shadow-2xs">
            <div>
              <p class="text-xs font-medium text-slate-500">Pending jobs</p>
              <p class="mt-1 text-2xl font-semibold text-slate-900 tabular-nums">{{ hostStatus.jobsPending }}</p>
            </div>
            @if (hostStatus.jobsPending > 0) {
              <button
                type="button"
                class="rounded-lg p-1.5 text-rose-600 transition hover:bg-rose-50 disabled:opacity-45"
                (click)="cleanupPending()"
                [disabled]="cleaningUpPending()"
                title="Delete pending non-user-data jobs"
              >
                @if (cleaningUpPending()) {
                  <app-spinner sizeClass="w-4 h-4" />
                } @else {
                  <app-icon name="delete-forever" sizeClass="w-4 h-4" />
                }
              </button>
            }
          </div>

          <div class="flex items-start justify-between rounded-2xl border border-slate-200 bg-white p-4 shadow-2xs">
            <div>
              <p class="text-xs font-medium text-slate-500">Running jobs</p>
              <p class="mt-1 text-2xl font-semibold text-indigo-700 tabular-nums">{{ hostStatus.jobsRunning }}</p>
            </div>
            @if (hostStatus.jobsRunning > 0) {
              <button
                type="button"
                class="rounded-lg p-1.5 text-amber-600 transition hover:bg-amber-50 disabled:opacity-45"
                (click)="forceAllRunning()"
                [disabled]="forcingAll()"
                title="Cancel and restart every running job outside the per-project lane limit"
              >
                @if (forcingAll()) {
                  <app-spinner sizeClass="w-4 h-4" />
                } @else {
                  <app-icon name="bolt" sizeClass="w-4 h-4" />
                }
              </button>
            }
          </div>

          <div
            class="flex items-start justify-between rounded-2xl border p-4 shadow-2xs"
            [class]="hostStatus.jobsFailed > 0 ? 'border-rose-200 bg-rose-50/60' : 'border-slate-200 bg-white'"
          >
            <div>
              <p class="text-xs font-medium" [class]="hostStatus.jobsFailed > 0 ? 'text-rose-700' : 'text-slate-500'">
                Failed jobs
              </p>
              <p
                class="mt-1 text-2xl font-semibold tabular-nums"
                [class]="hostStatus.jobsFailed > 0 ? 'text-rose-700' : 'text-slate-900'"
              >
                {{ hostStatus.jobsFailed }}
              </p>
            </div>
          </div>

          <div class="flex items-start justify-between rounded-2xl border border-slate-200 bg-white p-4 shadow-2xs">
            <div>
              <p class="text-xs font-medium text-slate-500">Completed jobs</p>
              <p class="mt-1 text-2xl font-semibold text-emerald-700 tabular-nums">{{ hostStatus.jobsDone }}</p>
            </div>
            @if (hostStatus.jobsDone > 0) {
              <button
                type="button"
                class="rounded-lg p-1.5 text-slate-500 transition hover:bg-slate-100 hover:text-slate-800 disabled:opacity-45"
                (click)="cleanupCompleted()"
                [disabled]="cleaningUpDone()"
                title="Remove finished (Done) jobs from queue"
              >
                @if (cleaningUpDone()) {
                  <app-spinner sizeClass="w-4 h-4" />
                } @else {
                  <app-icon name="delete" sizeClass="w-4 h-4" />
                }
              </button>
            }
          </div>
        </div>
      }

      <app-collapsible-section title="Failed jobs" [(expanded)]="failedExpanded">
        <span section-badges>
          @if (failedJobs().length > 0) {
            <app-badge tone="danger">{{ failedJobs().length }}</app-badge>
          } @else {
            <app-badge tone="neutral">0</app-badge>
          }
        </span>

        <div class="mb-3 flex flex-wrap items-center gap-2">
          <button
            type="button"
            class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-45"
            (click)="loadFailedJobs()"
            [disabled]="jobsLoading()"
          >
            <app-icon name="refresh" sizeClass="w-3.5 h-3.5" />
            <span>Refresh</span>
          </button>

          @if (failedJobs().length > 0) {
            <button
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg border border-indigo-200 bg-indigo-50 px-3 py-1.5 text-xs font-medium text-indigo-700 transition hover:bg-indigo-100 disabled:opacity-45"
              (click)="retryAllFailed()"
              [disabled]="retryingAll()"
            >
              @if (retryingAll()) {
                <app-spinner sizeClass="w-3.5 h-3.5" />
              } @else {
                <app-icon name="refresh" sizeClass="w-3.5 h-3.5" />
              }
              <span>Retry all</span>
            </button>
          }

          @if (jobsLoading() && !initialJobsLoading()) {
            <div class="w-28">
              <app-progress-bar />
            </div>
          }
        </div>

        @if (initialJobsLoading()) {
          <app-progress-bar />
        } @else if (jobsError()) {
          <p class="py-5 text-center text-xs text-rose-600">
            Could not load failed jobs — is Context.Dashboard.Server running?
          </p>
        } @else if (failedJobs().length === 0) {
          <p class="py-5 text-center text-xs text-slate-500">No failed jobs.</p>
        } @else {
          <div class="overflow-x-auto rounded-xl border border-slate-200">
            <table class="w-full text-left text-xs">
              <thead class="bg-slate-50 font-medium text-slate-500 uppercase">
                <tr>
                  <th class="px-3.5 py-2.5">Type</th>
                  <th class="px-3.5 py-2.5">Attempts</th>
                  <th class="px-3.5 py-2.5">Last error</th>
                  <th class="px-3.5 py-2.5">Created</th>
                  <th class="px-3.5 py-2.5 text-right">Action</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (job of failedJobs(); track job.id) {
                  <tr class="hover:bg-slate-50/70">
                    <td class="px-3.5 py-2.5 font-mono text-slate-800">{{ job.type }}</td>
                    <td class="px-3.5 py-2.5 text-slate-600">{{ job.attempts }}</td>
                    <td class="max-w-md px-3.5 py-2.5 text-rose-700 break-words">{{ job.lastError || '—' }}</td>
                    <td class="px-3.5 py-2.5 text-slate-500 whitespace-nowrap">{{ job.createdAt | date: 'short' }}</td>
                    <td class="px-3.5 py-2.5 text-right">
                      <button
                        type="button"
                        class="inline-flex items-center gap-1 rounded-lg bg-indigo-50 px-2.5 py-1 font-medium text-indigo-700 transition hover:bg-indigo-100 disabled:opacity-45"
                        (click)="retryJob(job)"
                        [disabled]="retryingJobId() === job.id"
                      >
                        @if (retryingJobId() === job.id) {
                          <app-spinner sizeClass="w-3 h-3" />
                        }
                        <span>Retry</span>
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </app-collapsible-section>

      <app-collapsible-section title="Pending jobs by type" [(expanded)]="pendingExpanded">
        <span section-badges>
          @if (pendingByType().length > 0) {
            <app-badge tone="neutral">{{ pendingByType().length }}</app-badge>
          }
        </span>

        <div class="mb-3 flex items-center gap-2">
          <button
            type="button"
            class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-45"
            (click)="loadPendingByType()"
            [disabled]="pendingByTypeLoading()"
          >
            <app-icon name="refresh" sizeClass="w-3.5 h-3.5" />
            <span>Refresh</span>
          </button>
          @if (pendingByTypeLoading() && !initialPendingByTypeLoading()) {
            <div class="w-28">
              <app-progress-bar />
            </div>
          }
        </div>

        @if (initialPendingByTypeLoading()) {
          <app-progress-bar />
        } @else if (pendingByTypeError()) {
          <p class="py-5 text-center text-xs text-rose-600">
            Could not load pending jobs — is Context.Dashboard.Server running?
          </p>
        } @else if (pendingByType().length === 0) {
          <p class="py-5 text-center text-xs text-slate-500">No pending jobs.</p>
        } @else {
          <div class="overflow-x-auto rounded-xl border border-slate-200">
            <table class="w-full text-left text-xs">
              <thead class="bg-slate-50 font-medium text-slate-500 uppercase">
                <tr>
                  <th class="px-3.5 py-2.5">Type</th>
                  <th class="px-3.5 py-2.5 text-right">Pending</th>
                  <th class="px-3.5 py-2.5 text-right">Action</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (row of pendingByType(); track row.type) {
                  <tr class="hover:bg-slate-50/70">
                    <td class="px-3.5 py-2.5 font-mono text-slate-800">{{ row.type }}</td>
                    <td class="px-3.5 py-2.5 text-right font-medium text-slate-700">{{ row.count }}</td>
                    <td class="px-3.5 py-2.5 text-right">
                      @if (!isUserDataJobType(row.type)) {
                        <button
                          type="button"
                          class="inline-flex items-center gap-1 rounded-lg bg-rose-50 px-2.5 py-1 font-medium text-rose-700 transition hover:bg-rose-100 disabled:opacity-45"
                          (click)="cleanupPendingOfType(row.type, row.count)"
                          [disabled]="cleaningUpPendingType() !== null"
                          title="Delete every Pending job of this type"
                        >
                          @if (cleaningUpPendingType() === row.type) {
                            <app-spinner sizeClass="w-3 h-3" />
                          }
                          <span>Delete</span>
                        </button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </app-collapsible-section>

      <app-collapsible-section title="Running jobs" [(expanded)]="runningExpanded">
        <span section-badges>
          @if (runningJobs().length > 0) {
            <app-badge tone="primary">{{ runningJobs().length }}</app-badge>
          }
        </span>

        <div class="mb-3 flex flex-wrap items-center gap-2">
          <button
            type="button"
            class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-45"
            (click)="loadRunningJobs()"
            [disabled]="runningJobsLoading()"
          >
            <app-icon name="refresh" sizeClass="w-3.5 h-3.5" />
            <span>Refresh</span>
          </button>

          @if (runningJobs().length > 0) {
            <button
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg border border-amber-200 bg-amber-50 px-3 py-1.5 text-xs font-medium text-amber-800 transition hover:bg-amber-100 disabled:opacity-45"
              (click)="forceAllRunning()"
              [disabled]="forcingAll()"
            >
              @if (forcingAll()) {
                <app-spinner sizeClass="w-3.5 h-3.5" />
              } @else {
                <app-icon name="bolt" sizeClass="w-3.5 h-3.5" />
              }
              <span>Force all</span>
            </button>
          }

          @if (runningJobsLoading() && !initialRunningJobsLoading()) {
            <div class="w-28">
              <app-progress-bar />
            </div>
          }
        </div>

        @if (initialRunningJobsLoading()) {
          <app-progress-bar />
        } @else if (runningJobsError()) {
          <p class="py-5 text-center text-xs text-rose-600">
            Could not load running jobs — is Context.Dashboard.Server running?
          </p>
        } @else if (runningJobs().length === 0) {
          <p class="py-5 text-center text-xs text-slate-500">No running jobs.</p>
        } @else {
          <div class="overflow-x-auto rounded-xl border border-slate-200">
            <table class="w-full text-left text-xs">
              <thead class="bg-slate-50 font-medium text-slate-500 uppercase">
                <tr>
                  <th class="px-3.5 py-2.5">Type</th>
                  <th class="px-3.5 py-2.5">Attempts</th>
                  <th class="px-3.5 py-2.5">Started</th>
                  <th class="px-3.5 py-2.5">Previous error</th>
                  <th class="px-3.5 py-2.5 text-right">Action</th>
                </tr>
              </thead>
              <tbody class="divide-y divide-slate-100">
                @for (job of runningJobs(); track job.id) {
                  <tr class="hover:bg-slate-50/70">
                    <td class="px-3.5 py-2.5 font-mono text-slate-800">{{ job.type }}</td>
                    <td class="px-3.5 py-2.5 text-slate-600">{{ job.attempts }}</td>
                    <td class="px-3.5 py-2.5 text-slate-500 whitespace-nowrap">
                      {{ job.startedAt ? (job.startedAt | date: 'mediumTime') : '—' }}
                    </td>
                    <td class="max-w-md px-3.5 py-2.5 text-rose-700 break-words">{{ job.lastError || '—' }}</td>
                    <td class="px-3.5 py-2.5 text-right">
                      <button
                        type="button"
                        class="inline-flex items-center gap-1 rounded-lg bg-amber-50 px-2.5 py-1 font-medium text-amber-800 transition hover:bg-amber-100 disabled:opacity-45"
                        (click)="forceJob(job)"
                        [disabled]="forcingJobId() === job.id"
                        title="Cancel this attempt and restart immediately"
                      >
                        @if (forcingJobId() === job.id) {
                          <app-spinner sizeClass="w-3 h-3" />
                        }
                        <span>Force</span>
                      </button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </app-collapsible-section>
    </div>
  `,
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
  private lastRunningReloadMs = 0;
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
  private lastPendingReloadMs = 0;

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
      if (
        current?.jobsRunning !== counts.running &&
        this.runningJobsRequested() &&
        Date.now() - this.lastRunningReloadMs >= JOB_PANEL_RELOAD_MIN_MS
      ) {
        this.loadRunningJobs();
      }
      if (
        current?.jobsPending !== counts.pending &&
        this.pendingByTypeRequested() &&
        Date.now() - this.lastPendingReloadMs >= JOB_PANEL_RELOAD_MIN_MS
      ) {
        this.loadPendingByType();
      }
    });
  }

  protected loadFailedJobs(): void {
    this.failedJobsResource.reload();
  }

  protected loadPendingByType(): void {
    this.lastPendingReloadMs = Date.now();
    if (this.pendingByTypeRequested()) {
      this.pendingByTypeResource.reload();
    } else {
      this.pendingByTypeRequested.set(true);
    }
  }

  protected loadRunningJobs(): void {
    this.lastRunningReloadMs = Date.now();
    if (this.runningJobsRequested()) {
      this.runningJobsResource.reload();
    } else {
      this.runningJobsRequested.set(true);
    }
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
