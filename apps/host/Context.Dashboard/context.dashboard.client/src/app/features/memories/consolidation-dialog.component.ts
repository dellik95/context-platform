import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, Injector, afterNextRender, computed, effect, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../../core/api.service';
import {
  ConsolidationReportStatus,
  ReconsolidationGroup,
  ReconsolidationReportSummaryDto,
  ReconsolidationStats,
} from '../../core/models';
import { SignalrService } from '../../core/signalr.service';
import { ConfirmDialogService } from '../../shared/confirm-dialog/confirm-dialog.service';
import { BadgeComponent, BadgeTone } from '../../shared/ui/badge.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { ModalShellComponent } from '../../shared/ui/modal-shell.component';
import { ProgressBarComponent } from '../../shared/ui/progress-bar.component';
import { SpinnerComponent } from '../../shared/ui/spinner.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ProjectContextService } from '../projects/project-context.service';
import { ConsolidationGroupCardComponent } from './consolidation-group-card.component';

const FINISHED_STATUSES: readonly ConsolidationReportStatus[] = ['Applied', 'Discarded', 'Failed'];

function isFinished(status: ConsolidationReportStatus): boolean {
  return FINISHED_STATUSES.includes(status);
}

function deleteFailureMessage(httpStatus: number): string {
  switch (httpStatus) {
    case 404:
      return 'The report no longer exists.';
    case 409:
      return 'The report is being applied and cannot be deleted yet.';
    default:
      return 'Failed to delete the report.';
  }
}

function statusBadgeTone(status: ConsolidationReportStatus): BadgeTone {
  switch (status) {
    case 'Pending':
      return 'warning';
    case 'Applying':
      return 'primary';
    case 'Applied':
      return 'success';
    case 'Failed':
      return 'danger';
    default:
      return 'neutral';
  }
}

/**
 * Batch memory reconsolidation: queue a dry run, review its report group by group, then apply it
 * with some groups excluded or discard it. Runs are background jobs, so after queueing one the
 * reports list refetches on every job-count push until it changes.
 */
@Component({
  selector: 'app-consolidation-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    ModalShellComponent,
    IconComponent,
    SpinnerComponent,
    ProgressBarComponent,
    BadgeComponent,
    ConsolidationGroupCardComponent,
  ],
  templateUrl: './consolidation-dialog.component.html',
})
export class ConsolidationDialogComponent {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly signalr = inject(SignalrService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly ctx = inject(ProjectContextService);
  private readonly injector = inject(Injector);

  readonly closed = output<void>();

  private readonly reportsResource = this.api.reconsolidationReportsResource(this.ctx.projectId);
  protected readonly reports = computed(() => (this.reportsResource.hasValue() ? this.reportsResource.value() : []));
  protected readonly reportsLoading = this.reportsResource.isLoading;

  protected readonly selectedReportId = signal<string | null>(null);
  private readonly reportResource = this.api.reconsolidationReportResource(this.selectedReportId);
  protected readonly selected = computed(() => {
    const dto = this.reportResource.hasValue() ? this.reportResource.value() ?? null : null;
    return dto?.id === this.selectedReportId() ? dto : null;
  });
  protected readonly selectedLoading = this.reportResource.isLoading;
  protected readonly refreshing = computed(
    () =>
      (this.reportsLoading() && this.reports().length > 0) ||
      (this.selectedLoading() && this.selected() !== null),
  );

  protected readonly excluded = signal<ReadonlySet<number>>(new Set());
  protected readonly busyAction = signal<'dryRun' | 'apply' | 'discard' | 'delete' | 'clear' | null>(null);
  protected readonly busy = computed(() => this.busyAction() !== null);
  protected readonly deletingId = signal<string | null>(null);
  protected readonly finishedCount = computed(() => this.reports().filter((r) => isFinished(r.status)).length);

  private readonly awaitedSignature = signal<string | null>(null);
  private readonly signature = computed(() => this.reports().map((r) => `${r.id}:${r.status}`).join(','));

  protected readonly applicableGroups = computed(() => {
    const report = this.selected()?.report;
    return report ? report.groups.filter((g) => !g.ambiguous && !this.excluded().has(g.index)).length : 0;
  });

  protected readonly titles = computed<ReadonlyMap<string, string>>(() => {
    const map = new Map<string, string>();
    for (const group of this.selected()?.report.groups ?? []) {
      for (const source of group.sources) {
        map.set(source.id, source.title);
      }
    }
    return map;
  });

  constructor() {
    effect(() => {
      this.ctx.projectId();
      this.selectedReportId.set(null);
      this.excluded.set(new Set());
      this.awaitedSignature.set(null);
    });

    this.signalr.jobCountsChanged$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      if (this.awaitedSignature() !== null) {
        this.reportsResource.reload();
        if (this.selectedReportId()) {
          this.reportResource.reload();
        }
      }
    });

    effect(() => {
      const awaited = this.awaitedSignature();
      if (awaited !== null && this.signature() !== awaited) {
        this.awaitedSignature.set(null);
      }
    });

    effect(() => {
      if (this.reportsResource.error()) {
        this.toast.error('Failed to load consolidation reports.');
      }
    });
  }

  protected statusTone(status: ConsolidationReportStatus): BadgeTone {
    return statusBadgeTone(status);
  }

  protected dryRun(): void {
    this.busyAction.set('dryRun');
    this.api.triggerReconsolidation(this.ctx.projectId(), true).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.awaitedSignature.set(this.signature());
        this.toast.success('Dry run queued. Its report appears here when the job finishes.', 4000);
      },
      error: () => {
        this.busyAction.set(null);
        this.toast.error('Failed to queue the dry run.');
      },
    });
  }

  protected refresh(): void {
    this.reportsResource.reload();
  }

  protected select(reportId: string, row: HTMLElement): void {
    const opening = this.selectedReportId() !== reportId;
    this.setSelected(opening ? reportId : null);
    if (opening) {
      afterNextRender(() => row.scrollIntoView({ block: 'start', behavior: 'smooth' }), { injector: this.injector });
    }
  }

  private setSelected(reportId: string | null): void {
    this.excluded.set(new Set());
    this.selectedReportId.set(reportId);
  }

  protected toggleGroup(group: ReconsolidationGroup, included: boolean): void {
    this.excluded.update((current) => {
      const next = new Set(current);
      if (included) {
        next.delete(group.index);
      } else {
        next.add(group.index);
      }
      return next;
    });
  }

  protected apply(): void {
    const reportId = this.selectedReportId();
    if (!reportId) {
      return;
    }

    this.busyAction.set('apply');
    this.api.applyReconsolidation(reportId, [...this.excluded()]).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.awaitedSignature.set(this.signature());
        this.toast.success('Apply queued.', 3000);
      },
      error: () => {
        this.busyAction.set(null);
        this.toast.error('Failed to apply the report — it may no longer be pending.');
      },
    });
  }

  protected async discard(): Promise<void> {
    const reportId = this.selectedReportId();
    if (!reportId) {
      return;
    }

    const confirmed = await this.confirmDialog.open({
      title: 'Discard Report',
      message: 'Discard this consolidation report?',
      confirmLabel: 'Discard',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.busyAction.set('discard');
    this.api.discardReconsolidation(reportId).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.reportsResource.reload();
        this.reportResource.reload();
      },
      error: () => {
        this.busyAction.set(null);
        this.toast.error('Failed to discard the report — it may no longer be pending.');
      },
    });
  }

  protected async deleteReport(report: ReconsolidationReportSummaryDto): Promise<void> {
    const confirmed = await this.confirmDialog.open({
      title: 'Delete Report',
      message: `Delete this ${report.status.toLowerCase()} report permanently?`,
      confirmLabel: 'Delete',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.busyAction.set('delete');
    this.deletingId.set(report.id);
    this.api.deleteReconsolidationReport(report.id).subscribe({
      next: () => {
        this.busyAction.set(null);
        this.deletingId.set(null);
        if (this.selectedReportId() === report.id) {
          this.setSelected(null);
        }
        this.reportsResource.reload();
        this.toast.success('Report deleted.', 3000);
      },
      error: (err: HttpErrorResponse) => {
        this.busyAction.set(null);
        this.deletingId.set(null);
        if (err.status === 404) {
          this.reportsResource.reload();
        }
        this.toast.error(deleteFailureMessage(err.status));
      },
    });
  }

  protected async clearFinished(): Promise<void> {
    const count = this.finishedCount();
    const confirmed = await this.confirmDialog.open({
      title: 'Clear Finished Reports',
      message: `Delete ${count} applied, discarded and failed ${count === 1 ? 'report' : 'reports'}? Pending reports are kept.`,
      confirmLabel: 'Clear finished',
      confirmTone: 'danger',
    });
    if (!confirmed) {
      return;
    }

    this.busyAction.set('clear');
    this.api.cleanupReconsolidationReports().subscribe({
      next: (result) => {
        this.busyAction.set(null);
        const open = this.reports().find((r) => r.id === this.selectedReportId());
        if (open && isFinished(open.status)) {
          this.setSelected(null);
        }
        this.reportsResource.reload();
        this.toast.success(`Removed ${result.removed} ${result.removed === 1 ? 'report' : 'reports'}.`, 3000);
      },
      error: () => {
        this.busyAction.set(null);
        this.toast.error('Failed to clear the finished reports.');
      },
    });
  }

  protected statsLine(stats: ReconsolidationStats | null): string {
    if (!stats) {
      return '';
    }
    return (
      `${stats.groups} groups · ${stats.canonicals} canonicals from ${stats.sourcesMerged} sources · ` +
      `${stats.contradictions} contradictions · ${stats.retiredChangeLogs} change logs retired` +
      (stats.usedModel ? '' : ' · heuristic only')
    );
  }
}
