import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api.service';
import { ReindexProgressEvent } from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { BadgeComponent } from '../../shared/ui/badge.component';
import { DonutChartComponent, DonutSegment } from '../../shared/ui/donut-chart.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ProjectContextService } from '../projects/project-context.service';
import { DashboardHooksCardComponent } from './dashboard-hooks-card.component';
import { DashboardJobsCardComponent } from './dashboard-jobs-card.component';
import { LogsDialogComponent } from './logs-dialog.component';

const STATUS_RECONCILE_INTERVAL_MS = 120_000;
const UPTIME_TICK_MS = 15_000;
const PROGRESS_HIDE_DELAY_MS = 2000;
const PROGRESS_IDLE_TIMEOUT_MS = 15000;

@Component({
  selector: 'app-dashboard-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
    BadgeComponent,
    EmptyStateComponent,
    DonutChartComponent,
    DashboardJobsCardComponent,
    DashboardHooksCardComponent,
    LogsDialogComponent,
  ],
  templateUrl: './dashboard-panel.component.html',
})
export class DashboardPanelComponent {
  private readonly api = inject(ApiService);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ctx = inject(ProjectContextService);

  private readonly statusResource = this.api.systemStatusResource();
  protected readonly status = computed(() => (this.statusResource.hasValue() ? this.statusResource.value() : null));
  protected readonly statusError = computed(() => Boolean(this.statusResource.error()));

  private readonly clockTick = signal(0);
  private readonly hostStartMs = signal<number | null>(null);
  protected readonly displayUptimeSeconds = computed(() => {
    this.clockTick();
    const startMs = this.hostStartMs();
    return startMs === null ? null : (Date.now() - startMs) / 1000;
  });

  protected readonly reindexingFull = signal(false);
  protected readonly reindexProgress = signal<ReindexProgressEvent | null>(null);
  protected readonly showReindexProgress = signal(false);
  protected readonly logsOpen = signal(false);

  protected readonly jobQueueSegments = computed<readonly DonutSegment[]>(() => {
    const hostStatus = this.status();
    if (!hostStatus) {
      return [];
    }
    return [
      { label: 'Completed', value: hostStatus.jobsDone, color: '#10b981' },
      { label: 'Running', value: hostStatus.jobsRunning, color: '#4f46e5' },
      { label: 'Pending', value: hostStatus.jobsPending, color: '#f59e0b' },
      { label: 'Failed', value: hostStatus.jobsFailed, color: '#ef4444' },
    ];
  });

  private hideTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    const reconcileIntervalId = setInterval(() => this.statusResource.reload(), STATUS_RECONCILE_INTERVAL_MS);
    this.destroyRef.onDestroy(() => clearInterval(reconcileIntervalId));

    const clockIntervalId = setInterval(() => this.clockTick.update((tick) => tick + 1), UPTIME_TICK_MS);
    this.destroyRef.onDestroy(() => clearInterval(clockIntervalId));

    this.signalr.jobCountsChanged$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((counts) => {
      if (this.statusResource.hasValue()) {
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
      }
    });

    effect(() => {
      const seconds = this.status()?.uptimeSeconds;
      if (seconds !== undefined) {
        this.hostStartMs.set(Date.now() - seconds * 1000);
      }
    });

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

  protected reindex(full: boolean): void {
    this.reindexingFull.set(full);
    this.ctx.triggerIndex(full);
  }

  protected cancelIndex(): void {
    this.ctx.cancelIndex(() => this.showReindexProgress.set(false));
  }

  protected formatUptime(seconds: number): string {
    const totalMinutes = Math.floor(seconds / 60);
    const hours = Math.floor(totalMinutes / 60);
    const minutes = totalMinutes % 60;
    return hours <= 0 ? `${minutes}m` : `${hours}h ${minutes}m`;
  }

  protected reindexPercent(progress: ReindexProgressEvent): number {
    return progress.filesTotal > 0 ? Math.min(100, Math.round((progress.filesProcessed / progress.filesTotal) * 100)) : 0;
  }
}
